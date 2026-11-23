import { fireEvent, render } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";

import { I18nProvider } from "../../i18n";
import type { Issue } from "../../platform";
import { ProblemsDock } from "./ProblemsDock";

// The pinned bar grows to about 230px open; a fixed 96px scroll padding let
// it cover the control that had focus.
const original = Object.getOwnPropertyDescriptor(HTMLElement.prototype, "offsetHeight");

afterEach(() => {
  if (original !== undefined) Object.defineProperty(HTMLElement.prototype, "offsetHeight", original);
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
