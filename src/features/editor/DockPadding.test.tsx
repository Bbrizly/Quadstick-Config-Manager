import { fireEvent, render } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";

import { I18nProvider } from "../../i18n";
import type { Issue } from "../../platform";
import editorCss from "../../styles/editor.css?raw";
import { ProblemsDock } from "./ProblemsDock";

// The pinned bar grows to about 230px open; a fixed 96px scroll padding let
// it cover the control that had focus.
const original = Object.getOwnPropertyDescriptor(HTMLElement.prototype, "offsetHeight");
const originalClient = Object.getOwnPropertyDescriptor(HTMLElement.prototype, "clientHeight");

afterEach(() => {
  if (original !== undefined) Object.defineProperty(HTMLElement.prototype, "offsetHeight", original);
  if (originalClient !== undefined) Object.defineProperty(HTMLElement.prototype, "clientHeight", originalClient);
});

describe("problems dock height", () => {
  it("tells the workspace how tall it is, again when it opens", () => {
    Object.defineProperty(HTMLElement.prototype, "offsetHeight", {
      configurable: true,
      get(this: HTMLElement) {
        if (!this.classList.contains("problems-dock")) return 0;
        return this.querySelector(".problems-list") === null ? 72 : 231;
      },
    });
    const issue = { severity: "warning", kind: "x", cell: "A4", message: "m" } as unknown as Issue;
    const view = render(
      <I18nProvider initialPreference="en">
        <div className="editor-workspace">
          <ProblemsDock
            issues={[issue]}
            status={{ kind: "idle" } as never}
            onFocusIssue={() => undefined}
            onStatus={() => undefined}
          />
        </div>
      </I18nProvider>,
    );
    const workspace = view.container.querySelector<HTMLElement>(".editor-workspace")!;
    expect(workspace.style.getPropertyValue("--qcm-dock-h")).toBe("72px");
    fireEvent.click(view.container.querySelector(".problems-toggle")!);
    expect(workspace.style.getPropertyValue("--qcm-dock-h")).toBe("231px");
  });
});

// B-122: at 200% in a 900x620 window the workspace is 223px and the open bar
// was 229px, so every Tab stop outside it landed under it.
describe("open problems list height", () => {
  it("reads the real stylesheet", () => {
    expect(editorCss.length).toBeGreaterThan(1000);
  });

  it("leaves 112px of workspace above the bar and keeps 40px of list", () => {
    const flat = editorCss.replace(/\/\*[\s\S]*?\*\//gu, "");
    const body = /(?:^|\})\s*\.problems-list\s*\{([^}]*)\}/u.exec(flat)?.[1] ?? "";
    expect(body).toMatch(
      /height:\s*max\(40px,\s*min\(150px,\s*calc\(var\(--qcm-workspace-h,\s*1000px\)\s*-\s*var\(--qcm-dock-bar-h,\s*0px\)\s*-\s*120px\)\)\)/u,
    );
  });

  it("publishes the workspace height and the bar's own height", () => {
    Object.defineProperty(HTMLElement.prototype, "offsetHeight", {
      configurable: true,
      get(this: HTMLElement) {
        if (this.classList.contains("problems-list")) return 40;
        if (!this.classList.contains("problems-dock")) return 0;
        return this.querySelector(".problems-list") === null ? 83 : 146;
      },
    });
    Object.defineProperty(HTMLElement.prototype, "clientHeight", {
      configurable: true,
      get(this: HTMLElement) {
        return this.classList.contains("editor-workspace") ? 223 : 0;
      },
    });
    const issue = { severity: "warning", kind: "x", cell: "A4", message: "m" } as unknown as Issue;
    const view = render(
      <I18nProvider initialPreference="en">
        <div className="editor-workspace">
          <ProblemsDock
            issues={[issue]}
            status={{ kind: "idle" } as never}
            onFocusIssue={() => undefined}
            onStatus={() => undefined}
          />
        </div>
      </I18nProvider>,
    );
    const workspace = view.container.querySelector<HTMLElement>(".editor-workspace")!;
    expect(workspace.style.getPropertyValue("--qcm-workspace-h")).toBe("223px");
    expect(workspace.style.getPropertyValue("--qcm-dock-bar-h")).toBe("83px");
    // jsdom loads no stylesheet, so the dock's 8px gap is set here.
    view.container.querySelector<HTMLElement>(".problems-dock")!.style.rowGap = "8px";
    fireEvent.click(view.container.querySelector(".problems-toggle")!);
    // 146 open, less the 40px list and the 8px gap: the bar alone, as closed.
    expect(workspace.style.getPropertyValue("--qcm-dock-bar-h")).toBe("98px");
    expect(workspace.style.getPropertyValue("--qcm-dock-h")).toBe("146px");
  });
});
