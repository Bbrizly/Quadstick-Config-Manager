import { act, fireEvent, render, screen, within } from "@testing-library/react";
import axe from "axe-core";
import { useState } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { I18nProvider } from "../../i18n";
import type { Issue } from "../../platform";
import editorCss from "../../styles/editor.css?raw";
import { ProblemsDock, showRow } from "./ProblemsDock";

function issue(cell: string, kind = "unknown_output", severity: Issue["severity"] = "warning"): Issue {
  return { severity, kind, cell, message: `bad ${cell}`, fix: "pick another" } as unknown as Issue;
}

const ISSUES = [
  issue("A4"),
  issue("C5", "unknown_input"),
  issue("A6"),
  issue("A7"),
  issue("A8"),
];

const IGNORE = (): void => undefined;

function Harness({
  initial,
  onFocusIssue = IGNORE,
}: {
  readonly initial: readonly Issue[];
  readonly onFocusIssue?: ((picked: Issue) => void) | undefined;
}) {
  // Moving a problem to notes takes it off the list, as the editor's refresh does.
  const [issues, setIssues] = useState(initial);
  return (
    <ProblemsDock
      issues={issues}
      status={{ kind: "info", text: "" }}
      onFocusIssue={onFocusIssue}
      onStatus={() => undefined}
      onMoveToNotes={(moved) => setIssues((all) => all.filter((each) => each !== moved))}
    />
  );
}

function open(issues: readonly Issue[] = ISSUES, onFocusIssue?: (picked: Issue) => void) {
  const view = render(
    <I18nProvider initialPreference="en">
      <Harness initial={issues} onFocusIssue={onFocusIssue} />
    </I18nProvider>,
  );
  fireEvent.click(view.container.querySelector(".problems-toggle")!);
  return view;
}

function rows(): HTMLElement[] {
  return within(screen.getByRole("listbox")).getAllByRole("option");
}

function picked(): HTMLElement {
  const selected = rows().filter((row) => row.getAttribute("aria-selected") === "true");
  expect(selected).toHaveLength(1);
  return selected[0]!;
}

function rule(selector: string): string {
  const flat = editorCss.replace(/\/\*[\s\S]*?\*\//gu, "");
  const escaped = selector.replace(/[.*+?^${}()|[\]\\]/gu, "\\$&");
  return new RegExp(`(?:^|\\})\\s*${escaped}\\s*\\{([^}]*)\\}`, "u").exec(flat)?.[1] ?? "";
}

afterEach(() => {
  vi.restoreAllMocks();
});

// With a big file open the list held 2320 problem buttons and 1280 fix
// buttons, all before the toggle that closes it.
describe("problems list keyboard", () => {
  it("is one Tab stop, with no buttons inside it", () => {
    open();
    const list = screen.getByRole("listbox");
    expect(list.querySelectorAll("button")).toHaveLength(0);
    const stops = [...list.querySelectorAll<HTMLElement>("*")].filter((element) => element.tabIndex >= 0);
    expect(stops).toEqual([rows()[0]]);
  });

  it("moves the pick and focus with the arrows, Home, End and the page keys", () => {
    open();
    rows()[0]!.focus();
    fireEvent.keyDown(document.activeElement!, { key: "ArrowDown" });
    expect(picked()).toBe(rows()[1]);
    expect(rows()[1]).toHaveFocus();
    expect(rows()[1]).toHaveAttribute("tabindex", "0");
    expect(rows()[0]).toHaveAttribute("tabindex", "-1");
    fireEvent.keyDown(document.activeElement!, { key: "End" });
    expect(picked()).toBe(rows()[4]);
    expect(rows()[4]).toHaveFocus();
    fireEvent.keyDown(document.activeElement!, { key: "ArrowDown" });
    expect(picked()).toBe(rows()[4]);
    fireEvent.keyDown(document.activeElement!, { key: "Home" });
    expect(picked()).toBe(rows()[0]);
    // No layout in jsdom, so a page is ten rows: past the end stops on the last.
    fireEvent.keyDown(document.activeElement!, { key: "PageDown" });
    expect(picked()).toBe(rows()[4]);
    fireEvent.keyDown(document.activeElement!, { key: "PageUp" });
    expect(picked()).toBe(rows()[0]);
    fireEvent.keyDown(document.activeElement!, { key: "ArrowUp" });
    expect(picked()).toBe(rows()[0]);
  });

  it("marks the pick with a check, not the tint alone", () => {
    open();
    expect(picked().querySelector(".problems-item-mark svg")).not.toBeNull();
    // Every row keeps the glyph and CSS shows only the pick's (WebKit relaid
    // out all 2320 rows when one was added).
    expect(rows()[1]!.querySelector(".problems-item-mark svg")).not.toBeNull();
    expect(rule('.problems-item:not([aria-selected="true"]) .problems-item-mark')).toMatch(/visibility:\s*hidden/u);
    expect(rule('.problems-item[aria-selected="true"]')).toMatch(/border-color:\s*var\(--qcm-accent\)/u);
  });

  it("picks with Enter as a click does: copies the problem and jumps to its cell", async () => {
    const writeText = vi.fn(() => Promise.resolve());
    Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText } });
    const onFocusIssue = vi.fn();
    open(ISSUES, onFocusIssue);
    rows()[0]!.focus();
    fireEvent.keyDown(document.activeElement!, { key: "ArrowDown" });
    await act(async () => {
      fireEvent.keyDown(document.activeElement!, { key: "Enter" });
      await Promise.resolve();
    });
    expect(onFocusIssue).toHaveBeenCalledWith(ISSUES[1]);
    expect(writeText).toHaveBeenCalledWith(expect.stringContaining("C5: bad C5"));

    fireEvent.click(within(rows()[3]!).getByText(/A7: bad A7/u));
    expect(onFocusIssue).toHaveBeenLastCalledWith(ISSUES[3]);
    expect(picked()).toBe(rows()[3]);
  });

  // Avalonia's name said selecting copies; here the arrows select and only
  // Enter copies, so the spoken name says which key does what.
  it("says in its name that Enter copies, not selecting", () => {
    open();
    const name = screen.getByRole("listbox").getAttribute("aria-label") ?? "";
    expect(name).toMatch(/Enter copies/u);
    expect(name).not.toMatch(/Select one to copy/u);
  });

  it("closes with Escape and puts focus back on the toggle", () => {
    const view = open();
    rows()[2]!.focus();
    fireEvent.keyDown(document.activeElement!, { key: "Escape" });
    expect(screen.queryByRole("listbox")).toBeNull();
    const toggle = view.container.querySelector<HTMLElement>(".problems-toggle")!;
    expect(toggle).toHaveFocus();
    expect(toggle).toHaveAttribute("aria-expanded", "false");
  });
});

describe("move to notes from the keyboard", () => {
  it("is one button outside the list that acts on the picked row", () => {
    open();
    const fix = screen.getByRole("button", { name: "Move the text in cell A4 to the notes column" });
    expect(screen.getByRole("listbox").contains(fix)).toBe(false);
    // On a row it cannot fix it stays, greyed and focusable, and does nothing.
    expect(fix).toHaveAttribute("aria-disabled", "true");
    fireEvent.click(fix);
    expect(rows()).toHaveLength(5);

    rows()[0]!.focus();
    fireEvent.keyDown(document.activeElement!, { key: "ArrowDown" });
    expect(fix).toHaveAccessibleName("Move the text in cell C5 to the notes column");
    expect(fix).toHaveAttribute("aria-disabled", "false");
    fireEvent.click(fix);
    expect(rows()).toHaveLength(4);
    expect(screen.queryByText(/C5: bad C5/u)).toBeNull();
    // The pick stays at its place, on the next problem, still one Tab stop.
    expect(picked()).toHaveTextContent("A6: bad A6");
    expect(picked()).toHaveAttribute("tabindex", "0");
  });

  it("says on a fixable row that the fix is below the list", () => {
    open();
    expect(rows()[1]).toHaveTextContent("Fix: Move to notes, below the list");
    expect(rows()[0]).not.toHaveTextContent("Move to notes");
  });

  it("keeps a pick and the focus when the last fixable row is fixed", () => {
    open([issue("A4"), issue("C5", "unknown_input")]);
    rows()[0]!.focus();
    fireEvent.keyDown(document.activeElement!, { key: "End" });
    const fix = screen.getByRole("button", { name: /cell C5/u });
    fix.focus();
    fireEvent.click(fix);
    expect(rows()).toHaveLength(1);
    expect(picked()).toHaveTextContent("A4: bad A4");
    // Nothing left to move: the button goes, and focus goes back to the pick.
    expect(screen.queryByRole("button", { name: /cell A4/u })).toBeNull();
    expect(picked()).toHaveFocus();
  });
});

describe("problems list rows", () => {
  it("are 36px or taller", () => {
    expect(editorCss.length).toBeGreaterThan(1000);
    const height = /min-height:\s*(\d+)px/u.exec(rule(".problems-item"))?.[1];
    expect(Number(height)).toBeGreaterThanOrEqual(36);
  });

  it("passes axe as a listbox", async () => {
    const view = open();
    const result = await axe.run(view.container, { rules: { "color-contrast": { enabled: false } } });
    expect(result.violations.map((violation) => violation.id)).toEqual([]);
  });
});

// The rows' offsetTop is measured from the workspace, not the list, so the
// first version scrolled the pick out of sight in a real browser.
function box(top: number, height: number, scrollTop = 0): HTMLElement {
  const element = document.createElement("div");
  element.scrollTop = scrollTop;
  element.getBoundingClientRect = () => ({ top, bottom: top + height }) as DOMRect;
  Object.defineProperty(element, "clientHeight", { value: height });
  Object.defineProperty(element, "offsetHeight", { value: height });
  return element;
}

describe("showing the picked row", () => {
  it("scrolls the list just enough, by where the row is on screen", () => {
    const list = box(500, 150, 1000);
    const below = box(620, 36);
    const focus = vi.spyOn(below, "focus");
    showRow(list, below);
    expect(list.scrollTop).toBe(1006);
    // The pinned workspace never scrolls with it.
    expect(focus).toHaveBeenCalledWith({ preventScroll: true });

    showRow(list, box(480, 36));
    expect(list.scrollTop).toBe(986);

    showRow(list, box(560, 36));
    expect(list.scrollTop).toBe(986);
  });
});
