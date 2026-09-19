import type { MessageKey } from "../../i18n";

/** One binding row as the grid exposes it (A=output, B=function, C–J=inputs, L=action name). */
export interface BindingCells {
  readonly row: number;
  readonly cells: readonly string[];
}

export type ZoneId =
  | "joystick"
  | "mp_left"
  | "mp_center"
  | "mp_right"
  | "combo"
  | "side"
  | "lip"
  | "jacks"
  | "other"
  | "settings"
  | "unset";

export interface GestureAction {
  readonly row: number;
  readonly output: string;
  readonly friendlyOutput: string;
  readonly functionName: string;
  readonly isSupport: boolean;
  readonly inputs: readonly string[];
}

export interface GestureSummary {
  readonly zone: ZoneId;
  readonly inputToken: string;
  readonly friendlyGestureKey: MessageKey;
  readonly actions: readonly GestureAction[];
  readonly hasComplexBehavior: boolean;
  readonly isMapped: boolean;
}

export interface JoystickSummary {
  readonly isRecognized: boolean;
  readonly roleKey: MessageKey | null;
  readonly roleToken: string;
  readonly actionCount: number;
  readonly extraActionCount: number;
}

const MOUTHPIECE_GESTURES: readonly { token: string; key: MessageKey }[] = [
  { token: "soft puff", key: "Main_SoftPuff" },
  { token: "puff", key: "Main_Puff" },
  { token: "sip", key: "Main_Sip" },
  { token: "soft sip", key: "Main_SoftSip" },
];

const LIP_GESTURES: readonly { token: string; key: MessageKey }[] = [
  { token: "lip", key: "Main_Lip" },
  { token: "soft lip", key: "Main_SoftLip" },
  { token: "push", key: "Main_Push" },
];

const LEFT_STICK: readonly { input: string; output: string }[] = [
  { input: "left", output: "left_joy_left" },
  { input: "right", output: "left_joy_right" },
  { input: "up", output: "left_joy_up" },
  { input: "down", output: "left_joy_down" },
];

const MOUSE: readonly { input: string; output: string }[] = [
  { input: "left", output: "mouse_left" },
  { input: "right", output: "mouse_right" },
  { input: "up", output: "mouse_up" },
  { input: "down", output: "mouse_down" },
];

export function zoneOf(input: string): ZoneId {
  if (input === "") return "unset";
  if (
    input.startsWith("mp_left_center") ||
    input.startsWith("mp_right_center") ||
    input.startsWith("mp_left_right") ||
    input.startsWith("mp_triple") ||
    input.startsWith("mp_right_mode")
  ) {
    return "combo";
  }
  if (input.startsWith("mp_left")) return "mp_left";
  if (input.startsWith("mp_center")) return "mp_center";
  if (input.startsWith("mp_right")) return "mp_right";
  if (["right_sip", "right_puff", "right_sip_soft", "right_puff_soft"].includes(input)) {
    return "side";
  }
  if (input === "lip" || input.startsWith("lip_")) return "lip";
  if (input.startsWith("digital_in")) return "jacks";
  if (
    ["left", "right", "up", "down", "any_direction", "center", "N", "NE", "E", "SE", "S", "SW", "W", "NW"].includes(
      input,
    ) ||
    input.endsWith("_inner")
  ) {
    return "joystick";
  }
  return "other";
}

/** Avalonia MainWindow.StripInput. */
export function stripInput(input: string, zoneId: string): string {
  if (input === "") return "";
  let s = input;
  if (zoneId !== "joystick" && zoneId !== "other") {
    const prefixes = [
      "mp_left_center_",
      "mp_right_center_",
      "mp_left_right_",
      "mp_triple_",
      "mp_right_mode_",
      "mp_left_",
      "mp_center_",
      "mp_right_",
      "right_",
    ];
    for (const prefix of prefixes) {
      if (s.startsWith(prefix)) {
        s = s.slice(prefix.length);
        break;
      }
    }
  }
  if (s.endsWith("_soft")) s = `soft ${s.slice(0, -5)}`;
  return s.replaceAll("_", " ");
}

export function humanize(token: string): string {
  const s = token.trim();
  if (s.length === 0) return s;
  const plain = s.replaceAll("_", " ");
  return plain.charAt(0).toUpperCase() + plain.slice(1);
}

/** Friendly words style (Avalonia labelStyle != 0, non-Xbox). */
export function tokenLabel(token: string): string {
  if (token === "ps3") return "PS";
  const plain = humanize(token);
  return plain.startsWith("Xac ") ? `XAC${plain.slice(3)}` : plain;
}

function functionName(fn: string): string {
  const f = fn.trim();
  const space = f.indexOf(" ");
  return space < 0 ? f : f.slice(0, space);
}

function rowInputs(cells: readonly string[]): string[] {
  return cells.slice(2, 10).map((value) => value.trim()).filter(Boolean);
}

function actionName(cells: readonly string[]): string {
  return cells[11]?.trim() ?? "";
}

function physicalBindings(rows: readonly BindingCells[], zone: ZoneId): BindingCells[] {
  return rows.filter((row) => {
    const inputs = rowInputs(row.cells);
    if (inputs.length === 0) return false;
    return inputs.some((input) => zoneOf(input) === zone);
  });
}

function toAction(row: BindingCells): GestureAction {
  const output = row.cells[0]?.trim() ?? "";
  const fn = row.cells[1]?.trim() ?? "";
  const name = actionName(row.cells);
  return {
    row: row.row,
    output,
    friendlyOutput: name.length > 0 ? name : tokenLabel(output),
    functionName: functionName(fn),
    isSupport: functionName(fn) === "force_off",
    inputs: rowInputs(row.cells),
  };
}

function gestureUses(binding: BindingCells, zone: ZoneId, gesture: string, inputPrefix = ""): boolean {
  return rowInputs(binding.cells).some((input) => {
    if (inputPrefix !== "" && !input.startsWith(inputPrefix)) return false;
    return stripInput(input, zone) === gesture;
  });
}

/** Avalonia DeviceSummary.Mouthpiece. */
export function mouthpiece(
  rows: readonly BindingCells[],
  zone: ZoneId,
  inputPrefix = "",
): GestureSummary[] {
  const gestures = zone === "lip" ? LIP_GESTURES : MOUTHPIECE_GESTURES;
  const bindings = physicalBindings(rows, zone);
  return gestures.map((gesture) => {
    const matches = bindings.filter((binding) => gestureUses(binding, zone, gesture.token, inputPrefix));
    const actions = matches.map(toAction);
    const sequences = matches.filter((binding) => rowInputs(binding.cells).length > 1);
    const functions = new Set(matches.map((binding) => functionName(binding.cells[1] ?? "")));
    const hasComplexBehavior =
      actions.some((action) => action.isSupport) || sequences.length > 0 || functions.size > 1;
    const isMapped = actions.some((action) => !action.isSupport && action.friendlyOutput.length > 0);
    return {
      zone,
      inputToken: gesture.token,
      friendlyGestureKey: gesture.key,
      actions,
      hasComplexBehavior,
      isMapped,
    };
  });
}

function tryCore(
  bindings: readonly BindingCells[],
  expected: readonly { input: string; output: string }[],
): Set<number> | null {
  const core = new Set<number>();
  for (const pair of expected) {
    const matches = bindings.filter((binding) => {
      const inputs = rowInputs(binding.cells);
      return (
        inputs.length === 1 &&
        inputs[0] === pair.input &&
        (binding.cells[0]?.trim() ?? "") === pair.output &&
        functionName(binding.cells[1] ?? "") === "normal"
      );
    });
    if (matches.length !== 1) return null;
    core.add(matches[0]!.row);
  }
  return core;
}

/** Avalonia DeviceSummary.Joystick. */
export function joystickSummary(rows: readonly BindingCells[]): JoystickSummary {
  const bindings = physicalBindings(rows, "joystick");
  const leftCore = tryCore(bindings, LEFT_STICK);
  if (leftCore !== null) {
    return {
      isRecognized: true,
      roleKey: "Main_LeftStick",
      roleToken: "left_stick",
      actionCount: bindings.length,
      extraActionCount: bindings.length - leftCore.size,
    };
  }
  const mouseCore = tryCore(bindings, MOUSE);
  if (mouseCore !== null) {
    return {
      isRecognized: true,
      roleKey: "Main_Mouse",
      roleToken: "",
      actionCount: bindings.length,
      extraActionCount: bindings.length - mouseCore.size,
    };
  }
  return {
    isRecognized: false,
    roleKey: null,
    roleToken: "",
    actionCount: bindings.length,
    extraActionCount: 0,
  };
}

export function summaryActionText(
  summary: GestureSummary,
  pluralAction: (count: number) => string,
  sequenceLabel: string,
): string {
  const names = [
    ...new Set(
      summary.actions
        .filter((action) => !action.isSupport && action.friendlyOutput.length > 0)
        .map((action) => action.friendlyOutput),
    ),
  ];
  if (names.length === 0) return "—";
  if (summary.hasComplexBehavior) {
    const multiInput = summary.actions.some((action) => action.inputs.length > 1);
    if (multiInput && summary.actions.length === 1) {
      return `${names[0]!} · ${sequenceLabel}`;
    }
    return `${names[0]!} · ${pluralAction(summary.actions.length)}`;
  }
  if (names.length <= 4) return names.join(" · ");
  return `${names.slice(0, 3).join(" · ")} · +${String(names.length - 3)}`;
}

export function zonesForRow(row: BindingCells): readonly ZoneId[] {
  const inputs = rowInputs(row.cells);
  if (inputs.length === 0) return ["unset"];
  return [...new Set(inputs.map(zoneOf))];
}
