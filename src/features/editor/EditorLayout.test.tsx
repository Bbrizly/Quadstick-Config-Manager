import { render } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { I18nProvider } from "../../i18n";
import { MockQcmClient } from "../../platform";
import editorCss from "../../styles/editor.css?raw";
import { EditorWorkspace } from "./EditorWorkspace";

// jsdom does no layout, so the rule is pinned in the stylesheet and the DOM it
// relies on. The pixels were measured in Chromium and WebKit (B-089, B-090).

/** Top-level rules only: a media query's override is not the default. */
function rule(selector: string): string {
  const flat = editorCss
    .replace(/\/\*[\s\S]*?\*\//gu, "")
    .replace(/@media[^{]*\{(?:[^{}]*\{[^{}]*\})*[^{}]*\}/gu, "");
  const found = [...flat.matchAll(/([^{}]+)\{([^{}]*)\}/gu)].find((m) => m[1]!.trim() === selector);
  if (found === undefined) throw new Error(`no rule for ${selector}`);
  return found[2]!;
}

describe("editor layout", () => {
  it("reads the real stylesheet", () => {
    expect(editorCss.length).toBeGreaterThan(1000);
  });

  it("pins the view keys: only the body above them scrolls (B-090)", () => {
    expect(rule(".modes-panel")).toMatch(/overflow:\s*hidden/u);
    const body = rule(".modes-panel-body");
    expect(body).toMatch(/flex:\s*1 1 0/u);
    expect(body).toMatch(/min-height:\s*0/u);
    expect(body).toMatch(/overflow-y:\s*auto/u);
  });

  it("gives the picture the width, with a 264 floor on the inspector (B-089)", () => {
    expect(rule(".device-workspace")).toMatch(/grid-template-columns:\s*minmax\(0, 1fr\) minmax\(264px, 0\.25fr\)/u);
  });

  it("keeps the view block outside the scrolling body, after it", async () => {
    const client = new MockQcmClient();
    const opened = await client.newProfile("racing.csv");
    const { container } = render(
      <I18nProvider initialPreference="en">
        <EditorWorkspace client={client} snapshot={opened} onSnapshot={() => undefined} />
      </I18nProvider>,
    );
    const view = container.querySelector(".sidebar-view");
    expect(view?.parentElement?.classList.contains("modes-panel")).toBe(true);
    expect(view?.previousElementSibling?.classList.contains("modes-panel-body")).toBe(true);
  });
});
