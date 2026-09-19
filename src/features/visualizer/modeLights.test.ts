import { describe, expect, it } from "vitest";

import { modeLightsFor } from "./modeLights";

function show(mode: number): string {
  const lights = modeLightsFor(mode);
  if (lights === null) return "none";
  return lights
    .map((light) => {
      switch (light) {
        case "purple":
          return "P";
        case "blue":
          return "B";
        case "red":
          return "R";
        default:
          return ".";
      }
    })
    .join("");
}

describe("modeLightsFor (FW 2373)", () => {
  it("returns null outside the firmware table", () => {
    expect(modeLightsFor(0)).toBeNull();
    expect(modeLightsFor(35)).toBeNull();
  });

  it("lights the first five modes one purple each, left to right", () => {
    expect(show(1)).toBe("P....");
    expect(show(2)).toBe(".P...");
    expect(show(3)).toBe("..P..");
    expect(show(4)).toBe("...P.");
    expect(show(5)).toBe("....P");
  });

  it("uses the firmware special case for mode 15", () => {
    expect(show(15)).toBe("....R");
  });

  it("matches later Avalonia ModeLightsFixtures", () => {
    expect(show(10)).toBe("....B");
    expect(show(20)).toBe("BBBBB");
    expect(show(34)).toBe("RRRPR");
  });
});
