import type { useI18n } from "../../i18n";
import { COMBO_PLACES } from "./deviceDiagram";
import { zoneOf, type BindingCells } from "./deviceSummary";

type Translate = ReturnType<typeof useI18n>["t"];

export const COMBO_PREFIXES = [
  "mp_left_center_",
  "mp_right_center_",
  "mp_left_right_",
  "mp_right_mode_",
  "mp_triple_",
] as const;

export function comboPrefix(token: string): string {
  return COMBO_PREFIXES.find((prefix) => token.startsWith(prefix)) ?? "";
}

/** Avalonia SwitchJacks.Ports: which socket on the back carries each channel. */
export const JACK_PORTS = {
  top: ["digital_in_8", "digital_in_7"],
  lip: ["digital_in_5", "digital_in_6"],
  bottom: ["digital_in_1", "digital_in_2"],
  usbData: ["digital_in_3", "digital_in_4"],
} as const satisfies Record<string, readonly string[]>;

export type JackPort = keyof typeof JACK_PORTS;

function jackPortOf(input: string): JackPort | null {
  for (const [port, channels] of Object.entries(JACK_PORTS) as [JackPort, readonly string[]][]) {
    if (channels.includes(input)) return port;
  }
  return null;
}

/**
 * Avalonia `PartOf`: which part of a zone an input sits on, when the zone holds
 * more than one. Four sockets share the jacks zone and five hole pairings share
 * the combo zone; every other zone is its own part, so null.
 */
export function partOf(input: string): string | null {
  const zone = zoneOf(input);
  if (zone === "jacks") return jackPortOf(input);
  if (zone === "combo") {
    const prefix = comboPrefix(input);
    return prefix === "" ? null : prefix;
  }
  return null;
}

/** Avalonia `ComboPair`: a hole pairing named from the hole names. */
export function comboPairTitle(input: string, t: Translate): string {
  if (input.startsWith("mp_triple_")) return t("Main_AllThree");
  if (input.startsWith("mp_left_center_")) return t("Main_APlusB", [t("Main_Left"), t("Main_Center")]);
  if (input.startsWith("mp_right_center_")) return t("Main_APlusB", [t("Main_Right"), t("Main_Center")]);
  if (input.startsWith("mp_right_mode_")) return t("Main_RightSideTube");
  if (input.startsWith("mp_left_right_")) return t("Main_APlusB", [t("Main_Left"), t("Main_Right")]);
  return t("Main_Combos");
}

/** Avalonia `PartLabel`: the part an input sits on, in the reader's language. */
export function partLabel(input: string, t: Translate): string {
  const zone = zoneOf(input);
  if (zone === "combo") return comboPairTitle(input, t);
  if (zone !== "jacks") return "";
  switch (jackPortOf(input)) {
    case "top":
      return t("Jack_TopJack");
    case "lip":
      return t("Jack_LipJack");
    case "bottom":
      return t("Jack_BottomJack");
    case "usbData":
      return t("Jack_USBADataPins");
    case null:
      return "";
  }
}

/**
 * Avalonia lands the combo view on a pairing instead of on five unpicked ones:
 * the first pairing something is mapped on, else the first pairing.
 */
export function firstMappedCombo(rows: readonly BindingCells[]): string | null {
  const mapped = COMBO_PLACES.find((place) =>
    rows.some((row) =>
      row.cells.slice(2, 10).some((cell) => comboPrefix(cell.trim()) === comboPrefix(place.input)),
    ),
  );
  return mapped?.input ?? COMBO_PLACES[0]?.input ?? null;
}
