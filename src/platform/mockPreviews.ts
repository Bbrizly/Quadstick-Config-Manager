/**
 * Browser-only previews for the side-by-side shots: a plugged-in QuadStick
 * with a prefs.csv and a console that answers. Loaded only when the page asks
 * for it (`?device`), so neither the catalog nor this file is in the app's
 * first chunk, and the packaged app never loads it at all.
 */
import preferences from "../QuadStick.Format/Data/preferences.json";
import type { ConsoleClient } from "./consoleContracts";
import type { MockQcmClient } from "./mockQcmClient";
import type { QcmClient } from "./qcmClient";
import type { PreferenceCatalog, PreferenceDefinition, PreferenceEditorKind } from "./preferenceContracts";

/** `qcm_config::preferences::CATEGORY_ORDER`. */
const CATEGORIES = [
  "Joystick",
  "Sip and puff",
  "Lip sensor",
  "Mouse",
  "Sound and lights",
  "Bluetooth",
  "Inputs and outputs",
  "USB and compatibility",
  "Advanced",
];

/** `RenderPreview.SamplePrefs`, the file the Avalonia shot draws. */
const SAMPLE_PREFS =
  "Preferences\nprefs.csv\nPreference,Value,Units,Description\n" +
  "joystick_deflection_minimum,8,,\njoystick_deflection_maximum,25,,\njoystick_D_Pad_inner,25,,\n" +
  "joystick_D_Pad_outer,80,,\nsip_puff_threshold_soft,8,,\nsip_puff_threshold,40,,\n" +
  "sip_puff_maximum,70,,\nsip_puff_delay_soft,1300,,\nlip_position_minimum,8,,\n" +
  "lip_position_maximum,35,,\nmouse_speed,100,,\n";

type Raw = Partial<Omit<PreferenceDefinition, "options">> & {
  readonly name: string;
  readonly options?: readonly ({ readonly value: string; readonly label: string } | string)[];
};

function catalog(): PreferenceCatalog {
  const definitions = (preferences as readonly Raw[]).map((raw): PreferenceDefinition => ({
    name: raw.name,
    label: raw.label ?? raw.name,
    category: raw.category ?? "Advanced",
    editor: (raw.editor ?? "text") as PreferenceEditorKind,
    default: raw.default ?? null,
    minimum: raw.minimum ?? null,
    maximum: raw.maximum ?? null,
    unit: raw.unit ?? "",
    description: raw.description ?? "",
    options: (raw.options ?? []).map((option) =>
      typeof option === "string" ? { value: option, label: option } : option,
    ),
    modeOverride: raw.modeOverride ?? false,
    risk: raw.risk ?? "",
    source: raw.source ?? "",
    firmwareMayAddMore: raw.firmwareMayAddMore ?? false,
    alsoCalled: raw.alsoCalled ?? "",
    advanced: raw.advanced ?? false,
  }));
  return { categories: CATEGORIES, definitions };
}

/** What the Avalonia DeviceLink shot lists on a Mac with nothing paired. */
const previewConsole: ConsoleClient = {
  listConsolePorts: () =>
    Promise.resolve([{ id: "port-1", label: "/dev/cu.debug-console (IOService:/AppleARMPE/arm-io@10F00000/AppleH16GPIO)" }]),
  connectConsole: () => Promise.resolve({ kind: "connected", build: 2373 } as const),
  closeConsole: () => Promise.resolve(),
  chooseConsoleProfile: () => Promise.resolve(null),
  consoleSendProfile: () => Promise.resolve({ name: "", backup: null }),
  consoleStrengthBegin: () => Promise.resolve({ threshold: 60 }),
  consoleCalibrate: () => Promise.resolve(),
  consoleSetThreshold: () => Promise.resolve(),
  consoleStrengthEnd: () => Promise.resolve(),
};

export function seedDevicePreview(mock: MockQcmClient): void {
  mock.plugDevice("QUADSTICK", { "prefs.csv": SAMPLE_PREFS });
  const preview = catalog();
  // The mock implements neither: they are optional on the client.
  const client: { -readonly [K in keyof QcmClient]: QcmClient[K] } = mock;
  client.getPreferenceCatalog = () => Promise.resolve(preview);
  client.console = previewConsole;
}
