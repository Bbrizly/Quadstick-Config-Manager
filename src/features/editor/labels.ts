/**
 * The words the editor shows for a row's tokens: an input as the part and
 * what is done to it ("Left · sip"), an output in plain words ("Mouse left").
 * The grid uses these; a host can too, so its own lists read the same.
 */
import type { MessageKey } from "../../i18n";
import { comboPairTitle } from "../visualizer/parts";
import { humanize, stripInput, tokenLabel, zoneOf } from "../visualizer/deviceSummary";

/** `MainWindow._labelStyle`: raw list names, plain English, Xbox style. */
export type LabelStyle = 0 | 1 | 2;

type Translate = (key: MessageKey, values?: readonly unknown[]) => string;

const ZONE_DISPLAY: Partial<Record<string, MessageKey>> = {
  joystick: "Main_Joystick",
  mp_left: "Main_Left",
  mp_center: "Main_Center",
  mp_right: "Main_Right",
  side: "Main_SideTube",
  lip: "Main_LipSwitch",
  jacks: "Main_SwitchJacks",
};

/** `InputOptionLabel` for a row cell: the part, then what is done to it. */
export function inputLabel(token: string, style: LabelStyle, t: Translate): string {
  if (token === "" || style === 0) return token;
  const zone = zoneOf(token);
  const bare = humanize(stripInput(token, zone));
  if (bare === "") return bare;
  const first = bare.split(" ")[0] ?? "";
  const low = first === first.toUpperCase() ? bare : bare.charAt(0).toLowerCase() + bare.slice(1);
  if (zone === "other" || zone === "unset") return bare;
  const part = ZONE_DISPLAY[zone];
  if (part === undefined) return bare;
  return `${t(part)} · ${low}`;
}

/**
 * An input named with its whole place, for a line read away from the device
 * picture: a combo names its pairing ("Left + Center · sip"), every other
 * input reads as in the grid.
 */
export function placedInputLabel(token: string, t: Translate): string {
  const trimmed = token.trim();
  if (zoneOf(trimmed) !== "combo") return inputLabel(trimmed, 1, t);
  const bare = humanize(stripInput(trimmed, "combo"));
  const low = bare.charAt(0).toLowerCase() + bare.slice(1);
  return bare === "" ? comboPairTitle(trimmed, t) : `${comboPairTitle(trimmed, t)} · ${low}`;
}

/** An output as the grid shows it. */
export function outputLabel(token: string, style: LabelStyle = 1): string {
  const trimmed = token.trim();
  if (trimmed === "" || style === 0) return trimmed;
  return tokenLabel(trimmed);
}
