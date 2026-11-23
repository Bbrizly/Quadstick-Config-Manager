import { act, fireEvent, render, screen, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { I18nProvider } from "../../i18n";
import { MockQcmClient, type CommunityProfile } from "../../platform";
import { CommunityProfilesPage, FILTER_ANNOUNCE_DELAY_MS } from "./CommunityProfilesPage";
import communityCss from "./community.css?raw";

function profile(name: string, csvName: string, connection: string, pointer = ""): CommunityProfile {
  return { name, sheetId: `id-${name}`, csvName, connection, notes: "", pointer };
}

const PROFILES = [
  profile("Rocket League", "rocket_league.csv", "PC (USB)", "by Silas"),
  profile("Forza Horizon 5", "forza.csv", "Xbox"),
  profile("Minecraft Java", "minecraft.csv", "PC (USB)"),
  profile("Elden Ring", "elden_ring.csv", "Xbox", "by Silas"),
];

function renderPage(overrides: Partial<Record<string, unknown>> = {}) {
  const client = new MockQcmClient();
  Object.assign(client, {
    loadCommunityCatalog: () => Promise.resolve({ profiles: PROFILES, fromCache: false, skippedRows: 0 }),
    importCommunityProfile: () => Promise.reject(new Error("offline")),
    openCommunitySheet: () => Promise.resolve(),
    ...overrides,
  });
  return render(
    <I18nProvider>
      <CommunityProfilesPage client={client} onReview={() => undefined} />
    </I18nProvider>,
  );
}

function statusLine(container: HTMLElement): HTMLElement {
  return container.querySelector<HTMLElement>(".community-status [role=status]")!;
}

function rule(selector: string): string {
  const start = communityCss.indexOf(`${selector} {`);
  return communityCss.slice(start, communityCss.indexOf("}", start));
}

function typeQuery(value: string): void {
  fireEvent.change(screen.getByRole("searchbox"), { target: { value } });
}

afterEach(() => {
  vi.useRealTimers();
});

// CommunityProfilesView.Load: offline with no saved copy says so and points
// at Refresh, instead of "Loading..." forever.
describe("community profiles offline", () => {
  it("says the list could not be downloaded when there is no network and no copy", async () => {
    const { container } = renderPage({
      loadCommunityCatalog: () => Promise.reject(new Error("offline")),
    });
    expect(
      await screen.findByText(
        "The community list could not be downloaded, and there is no saved copy on this computer.",
      ),
    ).toBeInTheDocument();
    expect(screen.queryByText("Loading the community list...")).not.toBeInTheDocument();
    expect(screen.getByText(/Check your internet connection and press Refresh/u)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Refresh" })).toBeEnabled();
    // On screen, not only in a hidden region, and marked as a failure.
    const status = statusLine(container);
    expect(status).toHaveTextContent(/Check your internet connection/u);
    expect(status.closest(".community-status")).toHaveClass("is-error");
    expect(status.className).not.toContain("visually-hidden");
  });
});

describe("community status line", () => {
  it("shows a failed import on screen in words that say it failed, with an icon", async () => {
    const { container } = renderPage();
    await screen.findByRole("listbox");
    fireEvent.click(screen.getByRole("button", { name: "Import the selected profile into the editor" }));
    const status = statusLine(container);
    await vi.waitFor(() => expect(status).toHaveTextContent(/^Could not import Rocket League: /u));
    expect(status.className).not.toContain("visually-hidden");
    expect(status).toHaveAttribute("aria-live", "polite");
    expect(status.querySelector("svg")).not.toBeNull();
    // Docked under the buttons, as Avalonia docks `_status`.
    const actions = container.querySelector(".community-actions")!;
    expect(actions.compareDocumentPosition(status) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it("scrolls the line into view when it changes, still under reduced motion", async () => {
    const scroll = vi.fn();
    Element.prototype.scrollIntoView = scroll;
    try {
      const { container } = renderPage();
      await screen.findByRole("listbox");
      scroll.mockClear();
      fireEvent.click(screen.getByRole("button", { name: "Import the selected profile into the editor" }));
      await vi.waitFor(() => expect(statusLine(container)).toHaveTextContent(/^Could not import/u));
      expect(scroll.mock.contexts.at(-1)).toBe(container.querySelector(".community-status"));
      expect(scroll).toHaveBeenLastCalledWith({ block: "nearest", behavior: "smooth" });

      document.documentElement.dataset["reduceMotion"] = "true";
      fireEvent.click(screen.getByRole("button", { name: "Open the selected profile's Google Sheet in your browser" }));
      await vi.waitFor(() => expect(statusLine(container)).toHaveTextContent(/^Opened/u));
      expect(scroll).toHaveBeenLastCalledWith({ block: "nearest", behavior: "auto" });

      // The no-match hint leaves the search field where it is.
      scroll.mockClear();
      typeQuery("zzzz");
      await vi.waitFor(() => expect(statusLine(container)).toHaveTextContent(/Nothing matches/u));
      expect(scroll).not.toHaveBeenCalled();
    } finally {
      delete document.documentElement.dataset["reduceMotion"];
      Reflect.deleteProperty(Element.prototype, "scrollIntoView");
    }
  });

  it("is never shrunk by the page column, so a failure stays inside its box", () => {
    expect(rule(".community-status")).toMatch(/flex:\s*none/u);
  });

  it("says when opening the sheet worked", async () => {
    const { container } = renderPage();
    await screen.findByRole("listbox");
    fireEvent.click(screen.getByRole("button", { name: "Open the selected profile's Google Sheet in your browser" }));
    await vi.waitFor(() =>
      expect(statusLine(container)).toHaveTextContent("Opened the Rocket League sheet in your browser."),
    );
    expect(statusLine(container).querySelector("svg")).toBeNull();
  });
});

describe("community list", () => {
  it("is a listbox of options showing the name and its details, with the first picked", async () => {
    renderPage();
    const list = await screen.findByRole("listbox", { name: "Community profiles, use the arrow keys" });
    const options = within(list).getAllByRole("option");
    expect(options).toHaveLength(4);
    expect(options[0]).toHaveAttribute("aria-selected", "true");
    expect(options[0]).toHaveAttribute("tabindex", "0");
    expect(options[1]).toHaveAttribute("tabindex", "-1");
    expect(options[0]).toHaveAccessibleName("Rocket League. rocket_league.csv, PC (USB), by Silas");
    expect(options[0]!.querySelector(".community-row-name")).toHaveTextContent("Rocket League");
    expect(options[0]!.querySelector(".community-row-details")).toHaveTextContent("rocket_league.csv · PC (USB) · by Silas");
    // The pick is marked by more than its tint.
    expect(options[0]!.querySelector(".community-row-mark svg")).not.toBeNull();
    expect(options[1]!.querySelector(".community-row-mark svg")).toBeNull();
  });

  it("walks with the arrows, Home, End and the page keys, and focus follows the pick", async () => {
    renderPage();
    const list = await screen.findByRole("listbox");
    const options = () => within(list).getAllByRole("option");
    const picked = () => options().findIndex((option) => option.getAttribute("aria-selected") === "true");
    fireEvent.keyDown(options()[0]!, { key: "ArrowDown" });
    expect(picked()).toBe(1);
    expect(options()[1]).toHaveFocus();
    fireEvent.keyDown(options()[1]!, { key: "End" });
    expect(picked()).toBe(3);
    expect(options()[3]).toHaveFocus();
    fireEvent.keyDown(options()[3]!, { key: "ArrowDown" });
    expect(picked()).toBe(3);
    fireEvent.keyDown(options()[3]!, { key: "Home" });
    expect(picked()).toBe(0);
    fireEvent.keyDown(options()[0]!, { key: "PageDown" });
    expect(picked()).toBe(3);
    fireEvent.keyDown(options()[3]!, { key: "PageUp" });
    expect(picked()).toBe(0);
    fireEvent.keyDown(options()[0]!, { key: "e" });
    expect(picked()).toBe(3);
    expect(screen.getByRole("heading", { level: 2 })).toHaveTextContent("Elden Ring");
  });

  it("moves from the search box into the list with Down", async () => {
    renderPage();
    await screen.findByRole("listbox");
    fireEvent.keyDown(screen.getByRole("searchbox"), { key: "ArrowDown" });
    expect(screen.getAllByRole("option")[0]).toHaveFocus();
  });

  it("imports with Enter from the search box, through the button's gate", async () => {
    const importProfile = vi.fn(() => new Promise<never>(() => undefined));
    renderPage({ importCommunityProfile: importProfile });
    const list = await screen.findByRole("listbox");
    fireEvent.keyDown(screen.getByRole("searchbox"), { key: "Enter" });
    expect(importProfile).toHaveBeenCalledWith("id-Rocket League", "rocket_league.csv");
    importProfile.mockClear();
    // Still importing: Enter on a row does not start a second one.
    fireEvent.keyDown(within(list).getAllByRole("option")[0]!, { key: "Enter" });
    await act(async () => undefined);
    expect(importProfile).not.toHaveBeenCalled();
  });

  it("imports the picked row with Enter", async () => {
    const importProfile = vi.fn(() => new Promise<never>(() => undefined));
    renderPage({ importCommunityProfile: importProfile });
    const list = await screen.findByRole("listbox");
    fireEvent.keyDown(within(list).getAllByRole("option")[0]!, { key: "ArrowDown" });
    fireEvent.keyDown(within(list).getAllByRole("option")[1]!, { key: "Enter" });
    expect(importProfile).toHaveBeenCalledWith("id-Forza Horizon 5", "forza.csv");
  });

  it("selects on click without importing", async () => {
    const importProfile = vi.fn(() => Promise.reject(new Error("offline")));
    renderPage({ importCommunityProfile: importProfile });
    const list = await screen.findByRole("listbox");
    fireEvent.click(within(list).getAllByRole("option")[2]!);
    expect(within(list).getAllByRole("option")[2]).toHaveAttribute("aria-selected", "true");
    expect(importProfile).not.toHaveBeenCalled();
  });

  it("styles the pick with an accent edge, not the tint alone, and rows of 40px or more", () => {
    expect(rule('.community-row[aria-selected="true"]')).toMatch(/border-inline-start-color:\s*var\(--qcm-accent\)/u);
    expect(Number(/min-height:\s*(\d+)px/u.exec(rule(".community-row"))?.[1])).toBeGreaterThanOrEqual(40);
    expect(rule(".community-row:focus-visible")).toMatch(/outline:\s*2px solid var\(--qcm-focus\)/u);
  });
});

describe("community filter", () => {
  it("speaks the count once typing settles, not on every key", async () => {
    const { container } = renderPage();
    await screen.findByRole("listbox");
    vi.useFakeTimers();
    const announcer = () =>
      [...container.querySelectorAll<HTMLElement>(".live-region.visually-hidden")].map((node) => node.textContent);
    typeQuery("r");
    typeQuery("ro");
    expect(announcer()).toEqual([""]);
    act(() => vi.advanceTimersByTime(FILTER_ANNOUNCE_DELAY_MS - 50));
    expect(announcer()).toEqual([""]);
    act(() => vi.advanceTimersByTime(100));
    expect(announcer()).toEqual(['Showing 1 of 4 profiles matching "ro".']);
  });

  it("puts Nothing matches on the status line, and takes it back when something matches", async () => {
    const { container } = renderPage();
    await screen.findByRole("listbox");
    vi.useFakeTimers();
    typeQuery("zzz");
    act(() => vi.advanceTimersByTime(FILTER_ANNOUNCE_DELAY_MS));
    expect(statusLine(container)).toHaveTextContent(
      "Nothing matches that search. Clear the search box to see the whole list.",
    );
    expect(screen.queryByRole("listbox")).not.toBeInTheDocument();
    expect(screen.getByText('No profiles match "zzz".')).toBeInTheDocument();
    typeQuery("");
    act(() => vi.advanceTimersByTime(FILTER_ANNOUNCE_DELAY_MS));
    expect(statusLine(container)).toHaveTextContent("");
    expect(screen.getByRole("listbox")).toBeInTheDocument();
  });
});

describe("community help", () => {
  it("names the ? button as a question, not the page title", async () => {
    renderPage();
    await screen.findByRole("listbox");
    expect(screen.getByRole("button", { name: "How do community profiles work?" })).toBeInTheDocument();
  });
});
