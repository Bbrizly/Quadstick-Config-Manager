import { describe, expect, it } from "vitest";

import { comboGroups } from "./combos";

const row = (n: number, ...cells: string[]) => ({ row: n, cells: cells });

describe("comboGroups", () => {
  it("groups rows that share the same ordered inputs", () => {
    const rows = [
      row(4, "cross", "normal", "mp_left_sip"),
      row(5, "circle", "normal", " mp_left_sip "),
      row(6, "square", "normal", "mp_left_sip", "mp_left_puff"),
      row(7, "triangle", "normal", "mp_left_puff", "mp_left_sip"),
      row(8, "l1", "normal"),
      row(9, "r1", "normal"),
    ];
    const groups = comboGroups(rows);
    expect(groups.get(4)?.map((member) => member.row)).toEqual([4, 5]);
    expect(groups.get(5)).toBe(groups.get(4));
    expect(groups.get(6)?.map((member) => member.row)).toEqual([6]);
    expect(groups.get(7)?.map((member) => member.row)).toEqual([7]);
    expect(groups.get(8)?.map((member) => member.row)).toEqual([8]);
    expect(groups.get(9)?.map((member) => member.row)).toEqual([9]);
  });
});
