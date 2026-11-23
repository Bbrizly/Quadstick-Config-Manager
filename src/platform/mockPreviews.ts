/**
 * Browser-only previews for the side-by-side shots: a plugged-in QuadStick
 * with a prefs.csv and a console that answers. Loaded only when the page asks
 * for it (`?device`), so neither the catalog nor this file is in the app's
 * first chunk, and the packaged app never loads it at all.
 */
import preferences from "../QuadStick.Format/Data/preferences.json";
import type { CommunityProfile } from "./communityContracts";
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

/** `?community`: a downloaded catalog, so the page can be seen and audited. */
export function seedCommunityPreview(mock: MockQcmClient): void {
  const profiles: CommunityProfile[] = [
    ["Rocket League", "rocket_league.csv", "PC (USB)", "Silas", "Sip left for boost, puff right for jump. Mouth joystick steers."],
    ["Forza Horizon 5", "forza_horizon_5.csv", "Xbox", "", ""],
    ["Minecraft Java", "minecraft_java.csv", "PC (USB)", "", "Mouse on the joystick, lip switch to place."],
    ["Elden Ring", "elden_ring.csv", "Xbox", "Silas", ""],
    ["Stardew Valley", "stardew_valley.csv", "PC (USB)", "", ""],
    ["Halo Infinite", "halo_infinite.csv", "Xbox", "", ""],
    ["Celeste", "celeste.csv", "PC (USB)", "Silas", ""],
    ["Fortnite", "fortnite.csv", "Xbox", "", ""],
  ].map(([name, csvName, connection, by, notes], index) => ({
    name: name!,
    sheetId: `preview-${String(index)}`,
    csvName: csvName!,
    connection: connection!,
    notes: notes!,
    pointer: by === "" ? "" : `by ${by!}`,
  }));
  const client: { -readonly [K in keyof QcmClient]: QcmClient[K] } = mock;
  client.loadCommunityCatalog = () => Promise.resolve({ profiles, fromCache: false, skippedRows: 2 });
  client.importCommunityProfile = () => Promise.reject(new Error("The preview has no network."));
  client.openCommunitySheet = () => Promise.resolve();
}

export function seedDevicePreview(mock: MockQcmClient): void {
  mock.plugDevice("QUADSTICK", { "prefs.csv": SAMPLE_PREFS });
  const preview = catalog();
  // The mock implements neither: they are optional on the client.
  const client: { -readonly [K in keyof QcmClient]: QcmClient[K] } = mock;
  client.getPreferenceCatalog = () => Promise.resolve(preview);
  client.console = previewConsole;
  // A Mac with nothing set up, as the Avalonia shot is rendered on one.
  client.controller = {
    getControllerOptions: () =>
      Promise.resolve({
        kind: "mac",
        bridgeOn: false,
        bridgeState: null,
        macOn: false,
        macSandboxed: false,
        macCommand: null,
        macUndoCommand: null,
      }),
    setControllerBridge: () => Promise.resolve(false),
    setMacGamepad: (on: boolean) => Promise.resolve({ took: true, on }),
    subscribeControllerBridge: () => Promise.resolve({ dispose: () => undefined }),
  };
}

function pad(row: string[]): string[] {
  while (row.length < 11) row.push("");
  return row;
}

/**
 * The largest profile the device takes: 16 modes of 128 mappings. `?big`
 * opens it on the first Open, for timing the editor at its limit.
 */
export function bigProfileGrid(): string[][] {
  const outputs = ["cross", "circle", "square", "triangle", "l1", "r1", "l2", "r2", "dpad_N", "dpad_S"];
  const inputs = ["sip", "puff", "lip", "hard sip", "soft puff", "left_sip", "right_puff", "mp_left_sip"];
  const grid: string[][] = [];
  for (let mode = 0; mode < 16; mode += 1) {
    if (mode > 0) grid.push(pad([]));
    grid.push(pad(["Profile Name", "", `Mode ${String(mode + 1)}`, "", "", "", "", "", "", "", "Comments"]));
    grid.push(pad([mode === 0 ? "big.csv" : ""]));
    grid.push(pad(["PlayStation Outputs", "Function", "usb"]));
    for (let row = 0; row < 128; row += 1) {
      grid.push(pad([outputs[row % outputs.length]!, "normal", inputs[(row + mode) % inputs.length]!]));
    }
  }
  return grid;
}
