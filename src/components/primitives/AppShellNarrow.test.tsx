import { act, cleanup, render } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import appCss from "../../styles/app.css?raw";
import { I18nProvider } from "../../i18n";
import { AppShell, NARROW_SHELL, shellIsNarrow } from "./AppShell";

// B-105: Interface size is root zoom and media queries never see it, so a
// 900 window at 200% kept the wide header in 450 CSS pixels. The brand drew
// over Home and, with Community, the page scrolled 36px sideways.

let width = 0;

function renderShell() {
  vi.spyOn(HTMLElement.prototype, "offsetWidth", "get").mockImplementation(() => width);
  const view = render(
    <I18nProvider>
      <AppShell
        activeDestination="home"
        onNavigate={() => undefined}
        themePreference="system"
        onThemePreferenceChange={() => undefined}
        onOpenSettings={() => undefined}
      >
        <p>Page</p>
      </AppShell>
    </I18nProvider>,
  );
  return view.container.querySelector<HTMLElement>(".app-shell")!;
}

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe("shellIsNarrow", () => {
  it("cuts at 820 CSS pixels, not window pixels", () => {
    expect(NARROW_SHELL).toBe(820);
    expect(shellIsNarrow(820)).toBe(true);
    expect(shellIsNarrow(821)).toBe(false);
    // 900x620 at 200% and 1280x860 at 200%.
    expect(shellIsNarrow(450)).toBe(true);
    expect(shellIsNarrow(640)).toBe(true);
    // Not measured yet.
    expect(shellIsNarrow(0)).toBe(false);
  });
});

describe("AppShell narrow flag", () => {
  it("is set from the shell's CSS width and follows a resize", () => {
    width = 450;
    const shell = renderShell();
    expect(shell.dataset["narrow"]).toBe("true");

    width = 1280;
    act(() => {
      window.dispatchEvent(new Event("resize"));
    });
    expect(shell.dataset["narrow"]).toBeUndefined();

    width = 640;
    act(() => {
      window.dispatchEvent(new Event("resize"));
    });
    expect(shell.dataset["narrow"]).toBe("true");
  });
});

describe("app.css narrow header", () => {
  const css = appCss.replace(/\/\*[\s\S]*?\*\//gu, "");

  function body(selector: string): string {
    const escaped = selector.replace(/[.*+?^${}()|[\]\\]/gu, "\\$&");
    return new RegExp(`(?:^|\\})\\s*${escaped}\\s*\\{([^}]*)\\}`, "u").exec(css)?.[1] ?? "";
  }

  it("reads the stylesheet", () => {
    expect(appCss.length).toBeGreaterThan(1000);
  });

  it("keys the narrow header on data-narrow, not a width media query", () => {
    expect(css).not.toMatch(/@media[^{]*\bmax-width:\s*820px/u);
    expect(body(".app-shell[data-narrow] .shell-header")).toMatch(/gap:\s*var\(--qcm-space-sm\)/u);
    expect(css).toMatch(
      /\.app-shell\[data-narrow\] \.shell-brand-caption,\s*\.app-shell\[data-narrow\] \.appearance-picker\s*\{\s*display:\s*none;/u,
    );
  });

  it("clips the brand instead of painting it over the nav", () => {
    const copy = body(".shell-brand-copy");
    expect(copy).toMatch(/min-width:\s*0/u);
    expect(copy).toMatch(/overflow:\s*hidden/u);
    expect(body(".shell-brand,\n.shellbrandbutton")).toMatch(/min-width:\s*0/u);
    expect(body(".shell-brand-name,\n.shell-brand-caption")).toMatch(/text-overflow:\s*ellipsis/u);
  });
});
