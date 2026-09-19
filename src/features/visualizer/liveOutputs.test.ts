import { describe, expect, it } from "vitest";

import { liveRowsForOutputs, outputsFromMotion } from "./liveOutputs";

describe("outputsFromMotion (Avalonia Ps3Outputs)", () => {
  it("maps button 1 to square", () => {
    const out = outputsFromMotion({ x: 0, y: 0, buttons: [1] });
    expect(out.has("square")).toBe(true);
    expect(out.has("cross")).toBe(false);
  });

  it("lights left_joy_right past the on threshold", () => {
    const out = outputsFromMotion({ x: 0.35, y: 0, buttons: [] });
    expect(out.has("left_joy_right")).toBe(true);
    expect(out.has("left_joy_left")).toBe(false);
  });

  it("keeps an axis lit through hysteresis between off and on", () => {
    const previous = outputsFromMotion({ x: 0.4, y: 0, buttons: [] });
    const held = outputsFromMotion({ x: 0.25, y: 0, buttons: [] }, previous);
    expect(held.has("left_joy_right")).toBe(true);
    const released = outputsFromMotion({ x: 0.1, y: 0, buttons: [] }, previous);
    expect(released.has("left_joy_right")).toBe(false);
  });

  it("matches binding rows by output token", () => {
    const outputs = outputsFromMotion({ x: 0, y: 0, buttons: [1] });
    const lit = liveRowsForOutputs(
      [
        { row: 4, cells: ["square", "normal", "lip"] },
        { row: 5, cells: ["circle", "normal", "lip"] },
      ],
      outputs,
    );
    expect([...lit]).toEqual([4]);
  });
});
