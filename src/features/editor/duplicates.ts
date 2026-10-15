import type { BindingCells } from "../visualizer/deviceSummary";

/**
 * `DuplicateUses.cs`: how many rows of one mode carry each token. Per mode,
 * because a mode is what the device runs at one time; ordinal, like the
 * firmware's compare. Inputs count per cell, so a row naming one twice is
 * itself a repeat. A combo's own rows share their inputs by design (3ad22ce),
 * so a combo card subtracts them: only a use outside the card counts.
 */
export interface DuplicateCounts {
  output(token: string): number;
  input(token: string): number;
}

export const NO_DUPLICATES: DuplicateCounts = { output: () => 0, input: () => 0 };

function bump(into: Map<string, number>, raw: string | undefined): void {
  const token = (raw ?? "").trim();
  if (token.length > 0) into.set(token, (into.get(token) ?? 0) + 1);
}

export function duplicateCounts(rows: readonly BindingCells[]): DuplicateCounts {
  const outputs = new Map<string, number>();
  const inputs = new Map<string, number>();
  for (const row of rows) {
    bump(outputs, row.cells[0]);
    for (const cell of row.cells.slice(2, 10)) bump(inputs, cell);
  }
  return {
    output: (token) => outputs.get(token.trim()) ?? 0,
    input: (token) => inputs.get(token.trim()) ?? 0,
  };
}
