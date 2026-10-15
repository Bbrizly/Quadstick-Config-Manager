import { describe, expect, it } from "vitest";

import { firstMappedCombo, partOf } from "./parts";

describe("partOf", () => {
  it("names the socket or pairing inside a shared zone", () => {
    expect(partOf("digital_in_8")).toBe("top");
    expect(partOf("digital_in_7")).toBe("top");
    expect(partOf("digital_in_1")).toBe("bottom");
    expect(partOf("digital_in_3")).toBe("usbData");
    expect(partOf("mp_left_center_sip")).toBe("mp_left_center_");
    expect(partOf("mp_triple_puff")).toBe("mp_triple_");
    expect(partOf("mp_left_sip")).toBeNull();
    expect(partOf("lip")).toBeNull();
  });

  it("lands on the first pairing with a mapping", () => {
    expect(firstMappedCombo([{ row: 4, cells: ["x", "normal", "mp_right_mode_sip"] }])).toBe(
      "mp_right_mode_sip",
    );
    expect(firstMappedCombo([])).toBe("mp_left_center_sip");
  });
});
