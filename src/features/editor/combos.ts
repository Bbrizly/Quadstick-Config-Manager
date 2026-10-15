import type { BindingCells } from "../visualizer/deviceSummary";

function inputTokens(row: BindingCells): string[] {
  return row.cells
    .slice(2, 10)
    .map((cell) => cell.trim())
    .filter((cell) => cell.length > 0);
}

/**
 * Avalonia `ComboGroups`: rows that fire together. A button combo is several
 * rows in the sheet, one output each, sharing the same inputs; the device has
 * no other way to press two buttons off one sip. Keyed on row number. Input
 * order matters, because the device walks several inputs on a row in
 * sequence. A row with no inputs never joins a group.
 */
export function comboGroups<Row extends BindingCells>(
  rows: readonly Row[],
): ReadonlyMap<number, readonly Row[]> {
  const groups = new Map<number, readonly Row[]>();
  const byInputs = new Map<string, Row[]>();
  for (const row of rows) {
    const tokens = inputTokens(row);
    if (tokens.length === 0) {
      groups.set(row.row, [row]);
      continue;
    }
    const key = tokens.join("\u001f");
    const list = byInputs.get(key);
    if (list === undefined) byInputs.set(key, [row]);
    else list.push(row);
  }
  for (const list of byInputs.values()) {
    for (const row of list) groups.set(row.row, list);
  }
  return groups;
}
