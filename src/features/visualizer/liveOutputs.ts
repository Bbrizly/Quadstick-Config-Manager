/**
 * Avalonia LiveInput.Ps3Outputs — profile words the device is sending now.
 * Buttons from the HID report; left-stick axes from X/Y with hysteresis.
 * Hat and right-stick axes need native report fields not yet on LiveMotion.
 */

import type { LiveMotion } from "../../platform";
import { BUTTON_OUTPUTS } from "./OutputPrompt";

const AXIS_ON = 0.3;
const AXIS_OFF = 0.2;

const LEFT_JOY = [
  { push: (m: LiveMotion) => -m.x, token: "left_joy_left" },
  { push: (m: LiveMotion) => m.x, token: "left_joy_right" },
  { push: (m: LiveMotion) => -m.y, token: "left_joy_up" },
  { push: (m: LiveMotion) => m.y, token: "left_joy_down" },
] as const;

function axisLit(
  push: number,
  token: string,
  previous: ReadonlySet<string> | null,
): boolean {
  const wasLit = previous?.has(token) === true;
  return push >= (wasLit ? AXIS_OFF : AXIS_ON);
}

/** Every profile word lit by this report, aliases included. */
export function outputsFromMotion(
  motion: LiveMotion,
  previous: ReadonlySet<string> | null = null,
): ReadonlySet<string> {
  const out = new Set<string>();
  for (const button of motion.buttons) {
    const tokens = BUTTON_OUTPUTS[button];
    if (tokens === undefined) continue;
    for (const token of tokens) out.add(token);
  }
  for (const axis of LEFT_JOY) {
    if (axisLit(axis.push(motion), axis.token, previous)) out.add(axis.token);
  }
  return out;
}

export function liveRowsForOutputs(
  rows: readonly { row: number; cells: readonly string[] }[],
  outputs: ReadonlySet<string>,
): ReadonlySet<number> {
  const lit = new Set<number>();
  for (const row of rows) {
    const output = row.cells[0]?.trim() ?? "";
    if (output !== "" && outputs.has(output)) lit.add(row.row);
  }
  return lit;
}
