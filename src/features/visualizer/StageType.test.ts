import { describe, expect, it } from "vitest";

import visualizerCss from "./visualizer.css?raw";

// The stage is drawn at 910 units and scaled to fit, about 0.69 at 1280x860,
// so its type has to start larger than the app's to read 11px (B-089).
// jsdom does no layout: the rendered size was measured in WebKit per model.
function stageRule(): string {
  const flat = visualizerCss.replace(/\/\*[\s\S]*?\*\//gu, "");
  const found = [...flat.matchAll(/([^{}]+)\{([^{}]*)\}/gu)].find((m) => m[1]!.trim() === ".visualizer-stage");
  if (found === undefined) throw new Error("no .visualizer-stage rule");
  return found[2]!;
}

describe("stage type", () => {
  it("reads the real stylesheet", () => {
    expect(visualizerCss.length).toBeGreaterThan(1000);
  });

  it("sets callout type at 16 and titles at 17 design units", () => {
    expect(stageRule()).toMatch(/--qcm-small-size:\s*16px/u);
    expect(stageRule()).toMatch(/--qcm-body-size:\s*17px/u);
  });

  it("never scales the stage under 0.69, where 16-unit type is 11px", () => {
    expect(stageRule()).toMatch(/min-width:\s*626px/u);
  });
});
