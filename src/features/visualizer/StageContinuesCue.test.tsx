import { act, cleanup, render } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import tokenCss from "../../styles/tokens.css?raw";
import { I18nProvider } from "../../i18n";
import { MockQcmClient } from "../../platform";
import { continuesPastView, QuadStickVisualizer } from "./QuadStickVisualizer";
import visualizerCss from "./visualizer.css?raw";

// B-107: at 1024x700 the stage is 553px tall in a 360px scroller and macOS
// hides an idle scrollbar, so the cut-off picture looked like the whole one.

describe("continuesPastView", () => {
  it("counts CSS pixels past the view, with a pixel of slack", () => {
    expect(continuesPastView(844, 623, 1)).toBe(true);
    expect(continuesPastView(624, 623, 1)).toBe(false);
    expect(continuesPastView(623, 623, 1)).toBe(false);
    // Rects are zoomed: 3 zoomed pixels at 200% is 1.5 CSS pixels, 1.5 is 0.75.
    expect(continuesPastView(626, 623, 2)).toBe(true);
    expect(continuesPastView(624.5, 623, 2)).toBe(false);
  });
});

let frameBottom = 844;

function rect(top: number, bottom: number): DOMRect {
  return { top, bottom, left: 0, right: 500, width: 500, height: bottom - top, x: 0, y: top, toJSON: () => ({}) };
}

function renderInScroller() {
  vi.spyOn(HTMLElement.prototype, "offsetWidth", "get").mockReturnValue(500);
  vi.spyOn(HTMLElement.prototype, "clientHeight", "get").mockImplementation(function (this: HTMLElement) {
    return this.classList.contains("test-scroller") ? 360 : 0;
  });
  vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function (this: HTMLElement) {
    if (this.classList.contains("test-scroller")) return rect(263, 623);
    if (this.classList.contains("visualizer-stage-frame")) return rect(frameBottom - 553, frameBottom);
    return rect(0, 0);
  });
  const view = render(
    <div className="test-scroller" style={{ overflowX: "auto", overflowY: "auto" }}>
      <I18nProvider initialPreference="en">
        <QuadStickVisualizer
          client={new MockQcmClient()}
          rows={[]}
          selectedRow={null}
          selectedZone={null}
          modeName="Racing"
          modeNumber={1}
          model="fps"
          view="device"
          onSelectRow={() => undefined}
          onSelectZone={() => undefined}
        />
      </I18nProvider>
    </div>,
  );
  return {
    scroller: view.container.querySelector<HTMLElement>(".test-scroller")!,
    frame: view.container.querySelector<HTMLElement>(".visualizer-stage-frame")!,
  };
}

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe("stage continues cue", () => {
  it("marks the stage while it runs past the scroller and clears at the end", () => {
    frameBottom = 844;
    const { scroller, frame } = renderInScroller();
    expect(frame.querySelector(".stage-more-cue")?.getAttribute("aria-hidden")).toBe("true");
    expect(frame.dataset["continues"]).toBe("true");

    frameBottom = 623;
    act(() => {
      scroller.dispatchEvent(new Event("scroll"));
    });
    expect(frame.dataset["continues"]).toBeUndefined();

    frameBottom = 700;
    act(() => {
      scroller.dispatchEvent(new Event("scroll"));
    });
    expect(frame.dataset["continues"]).toBe("true");
  });
});

function tokens(start: string): Record<string, string> {
  const at = tokenCss.indexOf(start);
  const body = tokenCss.slice(at, tokenCss.indexOf("}", at));
  return Object.fromEntries([...body.matchAll(/--qcm-([a-z-]+):\s*#([0-9a-f]{6})\b/gu)].map((m) => [m[1]!, m[2]!]));
}

function hexRgb(hex: string): number[] {
  return [0, 2, 4].map((i) => Number.parseInt(hex.slice(i, i + 2), 16));
}

function lum(rgb: number[]): number {
  const [r, g, b] = rgb.map((v) => {
    const c = v / 255;
    return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * r! + 0.7152 * g! + 0.0722 * b!;
}

describe("visualizer.css cue", () => {
  const css = visualizerCss.replace(/\/\*[\s\S]*?\*\//gu, "");
  const cueRules = [...css.matchAll(/([^{}]*stage-more-cue[^{}]*)\{([^{}]*)\}/gu)].map((m) => m[2]!);

  it("reads the stylesheet", () => {
    expect(visualizerCss.length).toBeGreaterThan(1000);
    expect(cueRules.length).toBeGreaterThanOrEqual(3);
  });

  it("is sticky, still, and never takes a click", () => {
    const base = /\.stage-more-cue\s*\{([^}]*)\}/u.exec(css)?.[1] ?? "";
    expect(base).toMatch(/position:\s*sticky/u);
    expect(base).toMatch(/height:\s*0/u);
    expect(base).toMatch(/pointer-events:\s*none/u);
    for (const body of cueRules) {
      expect(body).not.toMatch(/animation|transition/u);
    }
  });

  it("only draws while the frame is marked, and has a forced-colours line", () => {
    expect(css).toMatch(/\.visualizer-stage-frame\[data-continues\] > \.stage-more-cue::before\s*\{[^}]*linear-gradient/u);
    expect(css).toMatch(
      /@media \(forced-colors: active\)\s*\{\s*\.visualizer-stage-frame\[data-continues\] > \.stage-more-cue::before\s*\{[^}]*CanvasText/u,
    );
  });

  it("keeps callout text at 4.5:1 under the darkest edge of the shadow", () => {
    expect(css).toMatch(/linear-gradient\(to top, var\(--qcm-cue-shade\), transparent\)/u);
    for (const block of [':root[data-theme="light"]', ':root[data-theme="dark"]']) {
      const t = tokens(block);
      // The shade is the theme's own text colour at a set alpha (tokens.css).
      const at = tokenCss.indexOf(block);
      const body = tokenCss.slice(at, tokenCss.indexOf("}", at));
      const shade = /--qcm-cue-shade:\s*rgb\((\d+) (\d+) (\d+) \/ (\d+)%\)/u.exec(body);
      expect(shade, `${block} cue-shade`).not.toBeNull();
      const pct = Number(shade![4]);
      const ink = [Number(shade![1]), Number(shade![2]), Number(shade![3])];
      expect(ink).toEqual(hexRgb(t["text"]!));
      const under = hexRgb(t["surface"]!).map((v, i) => v * (1 - pct / 100) + ink[i]! * (pct / 100));
      for (const name of ["text", "text-secondary", "accent-text", "warning", "error"]) {
        const [hi, lo] = [lum(hexRgb(t[name]!)), lum(under)].toSorted((a, b) => b - a);
        expect((hi! + 0.05) / (lo! + 0.05), `${block} ${name}`).toBeGreaterThanOrEqual(4.5);
      }
    }
  });
});
