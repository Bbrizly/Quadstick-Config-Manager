import { fireEvent, render, screen } from "@testing-library/react";
import axe from "axe-core";
import { afterEach, describe, expect, it, vi } from "vitest";

import { applyInterfaceScale } from "../../app/theme";
import { I18nProvider } from "../../i18n";
import { MockQcmClient } from "../../platform";
import editorCss from "../../styles/editor.css?raw";
import { stageRoom, verticalScroller } from "../visualizer/stageFit";
import { EditorWorkspace } from "./EditorWorkspace";
import { FULL_WIDTH, STACKED_WIDTH, editorFit, windowIsShort } from "./editorFit";

// B-096. Interface size is root zoom: media queries see the window, and
// WebKit's container queries see zoomed pixels, so at 200% in a 1280 window
// the editor kept its desktop columns and the stage got 76px. The widths
// below were measured in WebKit and Chromium; jsdom does no layout.

function rule(selector: string): string {
  const flat = editorCss.replace(/\/\*[\s\S]*?\*\//gu, "");
  const found = [...flat.matchAll(/([^{}]+)\{([^{}]*)\}/gu)].find((m) => m[1]!.trim() === selector);
  if (found === undefined) throw new Error(`no rule for ${selector}`);
  return found[2]!;
}

describe("editor fit", () => {
  afterEach(() => {
    vi.restoreAllMocks();
    applyInterfaceScale(100);
  });

  it("reads the real stylesheet", () => {
    expect(editorCss.length).toBeGreaterThan(1000);
  });

  it("picks the layout from the workspace's CSS width", () => {
    // Sidebar 304, stage 626 + 8 padding, inspector 264, plate 34.
    expect(STACKED_WIDTH).toBe(972);
    expect(FULL_WIDTH).toBe(1236);
    expect(editorFit(1280)).toBe("full");
    expect(editorFit(1024)).toBe("inspector-below"); // 1024 window, or 1280 at 125%
    expect(editorFit(900)).toBe("stacked");
    expect(editorFit(853)).toBe("stacked"); // 1280 at 150%
    expect(editorFit(640)).toBe("tight"); // 1280 at 200%
    expect(editorFit(0)).toBe("full"); // not laid out yet
  });

  it("calls a window short by its CSS height, zoom included", () => {
    expect(windowIsShort(650, 1)).toBe(true);
    expect(windowIsShort(700, 1)).toBe(false);
    expect(windowIsShort(860, 2)).toBe(true);
    expect(windowIsShort(860, 1.25)).toBe(false);
  });

  it("lets the stage fit whatever scrolls it, not the sideways stage scroller", () => {
    const outer = document.createElement("div");
    outer.style.overflowY = "auto";
    const sideways = document.createElement("div");
    sideways.style.overflowX = "auto";
    sideways.style.overflowY = "hidden";
    const stage = document.createElement("div");
    sideways.append(stage);
    outer.append(sideways);
    document.body.append(outer);
    expect(verticalScroller(stage)).toBe(outer);
    outer.remove();
  });

  it("measures the stage's room in CSS pixels under zoom, less a pinned bar", () => {
    const base = { boxHeight: 600, rectDelta: 56, zoom: 1, scrollTop: 0, paddingBottom: 12, covered: 0 };
    expect(stageRoom(base)).toBe(532);
    // At 200% a 56px rect gap is 28 CSS pixels, not 56.
    expect(stageRoom({ ...base, zoom: 2 })).toBe(560);
    expect(stageRoom({ ...base, covered: 70 })).toBe(462);
    expect(stageRoom({ ...base, rectDelta: 16, scrollTop: 40 })).toBe(532);
  });

  it("sets --qcm-zoom beside zoom, and clears both at 100%", () => {
    const root = document.createElement("div");
    applyInterfaceScale(200, root);
    expect(root.style.getPropertyValue("--qcm-zoom")).toBe("2");
    applyInterfaceScale(100, root);
    expect(root.style.getPropertyValue("--qcm-zoom")).toBe("");
    expect(root.style.zoom).toBe("");
  });

  it("fills the window in CSS pixels, so zoom does not double it", () => {
    expect(rule(".editor-route")).toMatch(/height:\s*calc\(100vh \/ var\(--qcm-zoom, 1\) - 86px - var\(--qcm-hairline\)\)/u);
  });

  it("keeps no viewport query in the editor's stylesheet", () => {
    expect(editorCss).not.toMatch(/@media[^{]*(max|min)-(width|height)/u);
  });

  it("puts the inspector under the stage below full width", () => {
    const sel = '.editor-workspace:not([data-fit="full"]) .device-workspace';
    expect(rule(sel)).toMatch(/grid-template-columns:\s*minmax\(0, 1fr\)/u);
    expect(rule(sel)).toMatch(/overflow-y:\s*auto/u);
  });

  it("stacks the whole plate when the sidebar no longer fits beside the stage", () => {
    const fit = '.editor-workspace:is([data-fit="stacked"], [data-fit="tight"])';
    expect(rule(fit)).toMatch(/overflow-y:\s*auto/u);
    expect(rule(`${fit} .editor-plate`)).toMatch(/grid-template-columns:\s*minmax\(0, 1fr\)/u);
    // Nothing in the plate may ride over the pinned bar, or hide focus under it.
    expect(rule(`${fit} .editor-plate`)).toMatch(/isolation:\s*isolate/u);
    expect(rule(fit)).toMatch(/scroll-padding-block-end:\s*calc\(var\(--qcm-dock-h, 80px\) \+ 16px\)/u);
    expect(rule(`${fit} .problems-dock`)).toMatch(/position:\s*sticky/u);
  });

  it("drops the plate's side margins at 200% so the 626 stage fits 640", () => {
    expect(rule('.editor-workspace[data-fit="tight"] .editor-plate')).toMatch(/margin-inline:\s*0/u);
    expect(rule('.editor-workspace[data-fit="tight"] .device-canvas')).toMatch(/padding-inline:\s*0/u);
  });

  it("scrolls the whole sidebar when short, keyed to data-short", () => {
    expect(rule(".editor-workspace[data-short] .modes-panel")).toMatch(/overflow-y:\s*auto/u);
  });

  it("marks the workspace with the fit its width calls for", async () => {
    vi.spyOn(HTMLElement.prototype, "offsetWidth", "get").mockImplementation(function (this: HTMLElement) {
      return this.classList.contains("editor-workspace") ? 640 : 0;
    });
    const client = new MockQcmClient();
    const opened = await client.newProfile("racing.csv");
    const { container } = render(
      <I18nProvider initialPreference="en">
        <EditorWorkspace client={client} snapshot={opened} onSnapshot={() => undefined} />
      </I18nProvider>,
    );
    expect(container.querySelector(".editor-workspace")?.getAttribute("data-fit")).toBe("tight");
  });
});

describe("editor landmarks and targets", () => {
  it("names the Parts region once: the canvas is no landmark of its own", async () => {
    const client = new MockQcmClient();
    const opened = await client.newProfile("racing.csv");
    const { container } = render(
      <I18nProvider initialPreference="en">
        <EditorWorkspace client={client} snapshot={opened} onSnapshot={() => undefined} />
      </I18nProvider>,
    );
    fireEvent.click(screen.getByRole("button", { name: /Show the parts as a/u }));
    expect(screen.getAllByRole("region", { name: "Parts" })).toHaveLength(1);
    expect(container.querySelector(".device-canvas")?.tagName).toBe("DIV");
    const result = await axe.run(container, { runOnly: ["landmark-unique"] });
    expect(result.violations).toEqual([]);
  });

  it("gives the Modes help button a 40px target, as tall as its neighbours", () => {
    const help = rule("button.mode-help");
    expect(help).toMatch(/min-height:\s*var\(--qcm-icon-button\)/u);
    expect(help).toMatch(/min-width:\s*var\(--qcm-icon-button\)/u);
  });
});
