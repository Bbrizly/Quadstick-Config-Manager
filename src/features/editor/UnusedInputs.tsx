import { useI18n, type MessageKey } from "../../i18n";
import { PICKER_INPUTS, REAR_JOYSTICK } from "../../platform";
import { stripInput, zoneOf, type ZoneId } from "../visualizer/deviceSummary";
import { Popover } from "./Popover";

interface Row {
  readonly cells: readonly string[];
}

const ZONES: readonly { readonly id: ZoneId; readonly title: MessageKey }[] = [
  { id: "joystick", title: "Main_Joystick" },
  { id: "mp_left", title: "Main_LeftMouthpieceHole" },
  { id: "mp_center", title: "Main_CenterMouthpieceHole" },
  { id: "mp_right", title: "Main_RightMouthpieceHole" },
  { id: "side", title: "Main_SideTube" },
  { id: "lip", title: "Main_LipSwitch" },
  { id: "combo", title: "Main_HoleCombos" },
  { id: "jacks", title: "Main_SwitchJacks" },
  { id: "other", title: "Main_USBDevices" },
];

const COMBO_SHORT: readonly (readonly [string, string])[] = [
  ["mp_triple_", "all 3 "],
  ["mp_left_center_", "L+C "],
  ["mp_right_center_", "R+C "],
  ["mp_right_mode_", "R+S "],
  ["mp_left_right_", "L+R "],
];

/**
 * `UnusedInputs`: the inputs this mode maps nothing to, on the parts the
 * model has (and any it maps anyway). A force_off row does not use its input:
 * it only turns off an output, so the input is still free.
 */
export function unusedInputs(rows: readonly Row[], modelZones: readonly string[]): string[] {
  const used = new Set<string>();
  const mapped = new Set<string>();
  for (const row of rows) {
    const fn = (row.cells[1] ?? "").trim().split(" ")[0] ?? "";
    for (const cell of row.cells.slice(2, 10)) {
      const input = cell.trim();
      if (input === "") continue;
      mapped.add(zoneOf(input));
      if (fn !== "force_off") used.add(input);
    }
  }
  const visible = (zone: ZoneId): boolean => zone === "other" || modelZones.includes(zone) || mapped.has(zone);
  return PICKER_INPUTS.filter((input) => !used.has(input) && visible(zoneOf(input)));
}

function chipLabel(token: string, zone: ZoneId, t: (key: MessageKey, values?: readonly unknown[]) => string): string {
  if (REAR_JOYSTICK.includes(token)) return t("Main_RearJoystickDirection", [token.slice("usb_1_".length)]);
  if (zone !== "combo") return stripInput(token, zone);
  const short = COMBO_SHORT.find(([prefix]) => token.startsWith(prefix))?.[1] ?? "L+R ";
  return short + stripInput(token, zone);
}

export interface UnusedButtonProps {
  readonly rows: readonly Row[];
  readonly modelZones: readonly string[];
  readonly disabled: boolean;
  readonly onPick: (input: string) => void;
}

/** "Unused (N)": the count on the label, the list grouped by part, each input starting a mapping. */
export function UnusedButton({ rows, modelZones, disabled, onPick }: UnusedButtonProps) {
  const { t, plural } = useI18n();
  const free = unusedInputs(rows, modelZones);
  return (
    <Popover
      label={t("Main_UnusedFree", [free.length])}
      name={plural("Count_UnusedInputOpens", free.length, [free.length])}
      className="quiet unused-button"
      disabled={disabled}
    >
      {(close) => (
        <div className="unused-flyout" tabIndex={-1}>
          <strong>
            {free.length === 0 ? t("Main_EveryInputOnYourQuadStick") : plural("Count_UnusedInput", free.length, [free.length])}
          </strong>
          {ZONES.map((zone) => {
            const inZone = free.filter((input) => zoneOf(input) === zone.id);
            if (inZone.length === 0) return null;
            return (
              <div className="unused-group" key={zone.id}>
                <span className="unused-zone">{t(zone.title)}</span>
                <div className="unused-chips">
                  {inZone.map((token) => (
                    <button
                      type="button"
                      className="quiet unused-chip"
                      key={token}
                      title={token}
                      aria-label={t("Main_MapTokenToANew", [token, t(zone.title)])}
                      onClick={() => {
                        close();
                        onPick(token);
                      }}
                    >
                      {chipLabel(token, zone.id, t)}
                    </button>
                  ))}
                </div>
              </div>
            );
          })}
        </div>
      )}
    </Popover>
  );
}
