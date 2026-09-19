import { describe, expect, it } from "vitest";

import {
  BUTTON_OUTPUTS,
  hasPromptArt,
  outputsForButtons,
  requiresTextLabel,
} from "./OutputPrompt";

describe("outputsForButtons (Avalonia LiveInput.Ps3Buttons)", () => {
  it("maps button 1 to square, not cross", () => {
    expect([...outputsForButtons([1])]).toEqual(expect.arrayContaining(["square", "X"]));
    expect(outputsForButtons([1]).has("cross")).toBe(false);
  });

  it("maps button 2 to x/A (Cross family)", () => {
    expect([...outputsForButtons([2])]).toEqual(expect.arrayContaining(["x", "A"]));
  });

  it("stops at button 13 (PS/guide), with no invented hat buttons", () => {
    expect(BUTTON_OUTPUTS[13]).toEqual(["ps3", "guide"]);
    expect(BUTTON_OUTPUTS[14]).toBeUndefined();
  });
});

describe("OutputPrompt art kinds", () => {
  it("draws stick directions without a text label", () => {
    expect(hasPromptArt("left_joy_up")).toBe(true);
    expect(requiresTextLabel("left_joy_up")).toBe(false);
  });

  it("draws shoulders as vector art", () => {
    expect(hasPromptArt("left_1")).toBe(true);
    expect(hasPromptArt("right_trigger")).toBe(true);
  });

  it("still requires text beside mouse silhouettes", () => {
    expect(requiresTextLabel("mouse_left")).toBe(true);
  });
});
