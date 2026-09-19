import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from "react";

import fpsPhoto from "../../QuadStick.App/Assets/QuadStickFPS.png";
import originalPhoto from "../../QuadStick.App/Assets/QuadStickOriginal.png";
import singletonPhoto from "../../QuadStick.App/Assets/QuadStickSingleton.png";
import backPhoto from "../../QuadStick.App/Assets/QuadStickBack.png";
import { useI18n, type MessageKey } from "../../i18n";
import type { LiveSnapshot, QcmClient } from "../../platform";
import {
  PILL_H,
  PILL_W,
  SMALL_PILL_H,
  STAGE_W,
  TOP_CALLOUT_GAP,
  comboHotspots,
  comboPoints,
  COMBO_PLACES,
  deviceBottomLabelY,
  devicePhotoY,
  deviceStageHeight,
  diagramFor,
  lightX,
  onPhoto,
  photoHeight,
  type DeviceDiagramSpec,
  type Hotspot,
  type QsModel,
} from "./deviceDiagram";
import { ledCss, modeLightsFor, type ModeLight } from "./modeLights";
import { OutputPrompt } from "./OutputPrompt";
import { liveRowsForOutputs, outputsFromMotion } from "./liveOutputs";
import {
  joystickSummary,
  mouthpiece,
  summaryActionText,
  zonesForRow,
  type BindingCells,
  type GestureSummary,
  type ZoneId,
} from "./deviceSummary";
import { GestureTable } from "./GestureTable";
import "./visualizer.css";

export type VisualizerBinding = BindingCells;

interface QuadStickVisualizerProps {
  readonly client: QcmClient;
  readonly rows: readonly VisualizerBinding[];
  readonly selectedRow: number | null;
  readonly selectedZone: ZoneId | null;
  readonly modeName: string;
  readonly modeNumber: number | null;
  readonly view: "device" | "parts" | "rows";
  readonly onSelectRow: (row: number) => void;
  readonly onSelectZone: (zone: ZoneId) => void;
  readonly model?: QsModel;
  readonly onLiveRows?: (rows: ReadonlySet<number>) => void;
}

interface ZoneMeta {
  readonly id: ZoneId;
  readonly titleKey: MessageKey;
  readonly shortKey: MessageKey;
}

const ZONE_META: readonly ZoneMeta[] = [
  { id: "joystick", titleKey: "Main_Joystick", shortKey: "Main_Joystick" },
  { id: "mp_left", titleKey: "Main_LeftMouthpieceHole", shortKey: "Main_Left" },
  { id: "mp_center", titleKey: "Main_CenterMouthpieceHole", shortKey: "Main_Center" },
  { id: "mp_right", titleKey: "Main_RightMouthpieceHole", shortKey: "Main_Right" },
  { id: "side", titleKey: "Main_SideTube", shortKey: "Main_SideTube" },
  { id: "lip", titleKey: "Main_LipSwitch", shortKey: "Main_LipSwitch" },
  { id: "combo", titleKey: "Main_HoleCombos", shortKey: "Main_Combos" },
  { id: "jacks", titleKey: "Main_SwitchJacks", shortKey: "Main_SwitchJacks" },
  { id: "other", titleKey: "Main_USBDevices", shortKey: "Main_USBDevices" },
  { id: "settings", titleKey: "Main_ModeSettings", shortKey: "Main_ModeSettings" },
  { id: "unset", titleKey: "Main_NoInputYet", shortKey: "Main_NoInputYet" },
];

const MOUTHPIECE_ZONES = new Set<ZoneId>(["mp_left", "mp_center", "mp_right", "side", "lip"]);

const COMBO_PREFIXES = [
  "mp_left_center_",
  "mp_right_center_",
  "mp_left_right_",
  "mp_right_mode_",
  "mp_triple_",
] as const;

const BACK_STAGE_W = 720;
const BACK_STAGE_H = 285;
const BACK_PHOTO_X = 150;
const BACK_PHOTO_Y = 27;
const BACK_PHOTO_W = 420;
const BACK_PHOTO_H = 228;
const BACK_PILL_W = 145;
const BACK_PILL_H = 54;

/** Avalonia MainWindow.BackSockets — measured off QuadStickBack.png. */
const BACK_SOCKETS = [
  {
    nameKey: "Jack_TopJack" as const,
    detailKey: "Main_OneSwitchIn8" as const,
    guideKey: "Jack_PlugOneSwitchIntoThe" as const,
    zone: "jacks" as ZoneId,
    seed: "digital_in_8",
    left: true,
    labelY: 44,
    fx: 0.1114,
    fy: 0.2963,
    channels: ["digital_in_8", "digital_in_7"],
  },
  {
    nameKey: "Jack_LipJack" as const,
    detailKey: "Main_OneSwitchIn5" as const,
    guideKey: "Jack_TheMiddleJackIsThe" as const,
    zone: "jacks" as ZoneId,
    seed: "digital_in_5",
    left: true,
    labelY: 122,
    fx: 0.1114,
    fy: 0.5,
    channels: ["digital_in_5", "digital_in_6"],
  },
  {
    nameKey: "Jack_BottomJack" as const,
    detailKey: "Main_OneSwitchIn1" as const,
    guideKey: "Jack_PlugOneSwitchIntoThe2" as const,
    zone: "jacks" as ZoneId,
    seed: "digital_in_1",
    left: true,
    labelY: 200,
    fx: 0.1114,
    fy: 0.7147,
    channels: ["digital_in_1", "digital_in_2"],
  },
  {
    nameKey: "Main_USBBPort" as const,
    detailKey: "Main_ToTheComputer" as const,
    guideKey: "Main_ToTheComputer" as const,
    zone: "other" as ZoneId,
    seed: null,
    left: false,
    labelY: 58,
    fx: 0.9005,
    fy: 0.3354,
    channels: [] as string[],
  },
  {
    nameKey: "Main_USBAPort" as const,
    detailKey: "Main_JoystickOrIn34" as const,
    guideKey: "Main_JoystickOrIn34" as const,
    zone: "other" as ZoneId,
    seed: "digital_in_3",
    left: false,
    labelY: 158,
    fx: 0.9107,
    fy: 0.6254,
    channels: ["digital_in_3", "digital_in_4"],
  },
] as const;

const MODEL_SHORT: Record<QsModel, string> = {
  fps: "FPS",
  original: "Original",
  singleton: "Singleton",
};

const PHOTOS: Record<QsModel, string> = {
  fps: fpsPhoto,
  original: originalPhoto,
  singleton: singletonPhoto,
};

function photoFor(model: QsModel): string {
  return PHOTOS[model];
}

function comboPrefix(token: string): string {
  return COMBO_PREFIXES.find((prefix) => token.startsWith(prefix)) ?? "";
}

function comboPairTitle(input: string, t: ReturnType<typeof useI18n>["t"]): string {
  if (input.startsWith("mp_triple_")) return t("Main_AllThree");
  if (input.startsWith("mp_left_center_")) return t("Main_APlusB", [t("Main_Left"), t("Main_Center")]);
  if (input.startsWith("mp_right_center_")) return t("Main_APlusB", [t("Main_Right"), t("Main_Center")]);
  if (input.startsWith("mp_right_mode_")) return t("Main_RightSideTube");
  if (input.startsWith("mp_left_right_")) return t("Main_APlusB", [t("Main_Left"), t("Main_Right")]);
  return t("Main_Combos");
}

function modelHasZone(diagram: DeviceDiagramSpec, zoneId: ZoneId): boolean {
  return diagram.zones.includes(zoneId);
}

function isForeignZone(zoneId: ZoneId, diagram: DeviceDiagramSpec): boolean {
  return zoneId !== "settings" && zoneId !== "unset" && !modelHasZone(diagram, zoneId);
}

function series(parts: readonly string[]): string {
  if (parts.length === 1) return parts[0]!;
  return `${parts.slice(0, -1).join(", ")} and ${parts[parts.length - 1]!}`;
}

/** Inner describe string for Main_DeviceShowsModeLightsDescribeLit (Avalonia ModeLights.Describe). */
function describeModeLights(lights: readonly ModeLight[]): string {
  const lit = lights
    .map((light, index) => ({ light, number: index + 1 }))
    .filter((entry) => entry.light !== "off");
  if (lit.length === 0) return "no lights";
  const groups = new Map<ModeLight, number[]>();
  for (const entry of lit) {
    const bucket = groups.get(entry.light) ?? [];
    bucket.push(entry.number);
    groups.set(entry.light, bucket);
  }
  return [...groups.entries()]
    .map(([light, numbers]) => {
      const label = numbers.length === 1 ? "light " : "lights ";
      return `${label}${series(numbers.map(String))} ${light}`;
    })
    .join(", ");
}

function liveJoystickActive(frame: LiveSnapshot | null): boolean {
  if (frame?.status.kind !== "reading") return false;
  return Math.abs(frame.status.motion.x) >= 0.12 || Math.abs(frame.status.motion.y) >= 0.12;
}

function liveText(frame: LiveSnapshot | null, t: ReturnType<typeof useI18n>["t"]): string {
  if (frame === null || frame.status.kind === "stopped" || frame.status.kind === "searching") {
    return t("DevicePage_NothingIsReadingTheStick");
  }
  if (frame.status.kind === "reading") {
    const pressed = frame.status.motion.buttons;
    return pressed.length === 0
      ? t("DevicePage_ReadingProductNothingPressed", [frame.status.product])
      : t("DevicePage_ReadingProductPressedNow", [frame.status.product, pressed.join(", ")]);
  }
  if (frame.status.kind === "xinputOnly") return t("Main_ThisEmulationModeIsNot");
  return t("DevicePage_NothingIsReadingTheStick");
}

function zoneAccessibleName(
  zone: ZoneMeta,
  rows: readonly BindingCells[],
  t: ReturnType<typeof useI18n>["t"],
  plural: ReturnType<typeof useI18n>["plural"],
): string {
  if (MOUTHPIECE_ZONES.has(zone.id)) {
    const gestures = mouthpiece(rows, zone.id);
    const parts = gestures.map((summary) => {
      const spoken = summaryActionText(
        summary,
        (count) => plural("Count_Action", count, [count]),
        t("Main_Sequence"),
      );
      return `${t(summary.friendlyGestureKey)}: ${spoken}`;
    });
    return `${t(zone.shortKey)}. ${parts.join(", ")}`;
  }
  if (zone.id === "joystick") {
    const summary = joystickSummary(rows);
    if (summary.isRecognized && summary.roleKey !== null) {
      return `${t(zone.shortKey)}. ${t("Main_Movement")}: ${t(summary.roleKey)}`;
    }
    return `${t(zone.shortKey)}. ${
      summary.actionCount === 0
        ? t("Main_NotMapped")
        : plural("Count_Action", summary.actionCount, [summary.actionCount])
    }`;
  }
  const count = rows.filter((row) => zonesForRow(row).includes(zone.id)).length;
  return `${t(zone.titleKey)}. ${
    count === 0 ? t("Main_NotMapped") : plural("Count_Mapping", count, [count])
  }`;
}

function comboAccessibleName(
  input: string,
  rows: readonly BindingCells[],
  t: ReturnType<typeof useI18n>["t"],
  plural: ReturnType<typeof useI18n>["plural"],
): string {
  const prefix = comboPrefix(input);
  const comboRows = rows.filter((row) =>
    row.cells.slice(2, 10).some((cell) => comboPrefix(cell.trim()) === prefix),
  );
  const count = comboRows.length;
  const countLabel = count === 0 ? t("Main_NotMapped") : plural("Count_Mapping", count, [count]);
  const gestures = mouthpiece(rows, "combo", prefix);
  const spoken = gestures
    .map((summary) => {
      const action = summaryActionText(
        summary,
        (n) => plural("Count_Action", n, [n]),
        t("Main_Sequence"),
      );
      return `${t(summary.friendlyGestureKey)}: ${action}`;
    })
    .join(", ");
  return `${t("Main_HolePairingPairCount", [comboPairTitle(input, t), countLabel])} ${spoken}`;
}

function gestureRowsLive(
  summaries: readonly GestureSummary[],
  liveRows: ReadonlySet<number>,
): boolean {
  return summaries.some((summary) => summary.actions.some((action) => liveRows.has(action.row)));
}

function zoneRowsLive(zoneRows: readonly BindingCells[], liveRows: ReadonlySet<number>): boolean {
  return zoneRows.some((row) => liveRows.has(row.row));
}

export function QuadStickVisualizer({
  client,
  rows,
  selectedRow,
  selectedZone,
  modeName,
  modeNumber,
  view,
  onSelectRow,
  onSelectZone,
  model = "fps",
  onLiveRows,
}: QuadStickVisualizerProps) {
  const { t, plural } = useI18n();
  const [practice, setPractice] = useState(false);
  const [combos, setCombos] = useState(false);
  const [pickedCombo, setPickedCombo] = useState<string | null>(null);
  const [live, setLive] = useState<LiveSnapshot | null>(null);
  const [liveOutputs, setLiveOutputs] = useState<ReadonlySet<string>>(new Set());
  const [focusedZone, setFocusedZone] = useState(0);
  const hotspotRefs = useRef<Array<HTMLButtonElement | null>>([]);
  const previousOutputs = useRef<ReadonlySet<string> | null>(null);
  const diagram = diagramFor(model);

  const showBack = selectedZone === "jacks" || selectedZone === "other";
  const showCombos =
    !showBack && (combos || selectedZone === "combo") && diagram.zones.includes("combo");

  const rowsByZone = useMemo(() => {
    const map = new Map<ZoneId, BindingCells[]>();
    for (const zone of ZONE_META) map.set(zone.id, []);
    for (const row of rows) {
      for (const zone of zonesForRow(row)) map.get(zone)?.push(row);
    }
    return map;
  }, [rows]);

  const foreignZones = useMemo(() => {
    return ZONE_META.filter(
      (zone) =>
        isForeignZone(zone.id, diagram) && (rowsByZone.get(zone.id)?.length ?? 0) > 0,
    );
  }, [diagram, rowsByZone]);

  const selectedZones = useMemo(() => {
    if (selectedZone !== null) return new Set<ZoneId>([selectedZone]);
    const row = selectedRow === null ? undefined : rows.find((candidate) => candidate.row === selectedRow);
    return new Set(row === undefined ? [] : zonesForRow(row));
  }, [rows, selectedRow, selectedZone]);

  const liveRows = useMemo(
    () => liveRowsForOutputs(rows, liveOutputs),
    [rows, liveOutputs],
  );

  useEffect(() => {
    onLiveRows?.(liveRows);
  }, [liveRows, onLiveRows]);

  useEffect(() => {
    let disposed = false;
    let subscription: { dispose(): void } | null = null;
    void client
      .startLiveInput((frame) => {
        if (disposed) return;
        setLive(frame);
        if (frame.status.kind === "reading") {
          const next = outputsFromMotion(frame.status.motion, previousOutputs.current);
          previousOutputs.current = next;
          setLiveOutputs(next);
        } else {
          previousOutputs.current = null;
          setLiveOutputs(new Set());
        }
      })
      .then((value) => {
        if (disposed) value.dispose();
        else subscription = value;
      })
      .catch(() => {
        if (!disposed) {
          setLive(null);
          setLiveOutputs(new Set());
        }
      });
    return () => {
      disposed = true;
      subscription?.dispose();
    };
  }, [client]);

  useEffect(() => {
    if (selectedZone === "combo" && !combos) setCombos(true);
  }, [selectedZone, combos]);

  // Avalonia auto-picks the first mapped pairing so hole rings appear immediately.
  useEffect(() => {
    if (!showCombos) return;
    if (pickedCombo !== null && comboPrefix(pickedCombo) !== "") return;
    const comboRows = rowsByZone.get("combo") ?? [];
    const mapped = COMBO_PLACES.find((place) =>
      comboRows.some((row) =>
        row.cells.slice(2, 10).some((cell) => comboPrefix(cell.trim()) === comboPrefix(place.input)),
      ),
    );
    setPickedCombo(mapped?.input ?? COMBO_PLACES[0]?.input ?? null);
  }, [showCombos, pickedCombo, rowsByZone]);

  const photoY = devicePhotoY(diagram);
  const photoH = photoHeight(diagram);
  const stageH = deviceStageHeight(diagram);
  const bottomY = deviceBottomLabelY(diagram);
  const calloutBottom = photoY - TOP_CALLOUT_GAP;
  const { source } = diagram;

  const photoZones = ZONE_META.filter((zone) => diagram.hotspots.some((spot) => spot.zone === zone.id));
  const comboSpots = comboHotspots(diagram);
  const normalSpots = photoZones.map((zone) => diagram.hotspots.find((spot) => spot.zone === zone.id)!);
  const stageSpots: readonly Hotspot[] = showCombos ? comboSpots : normalSpots;
  const stageSpotCount = stageSpots.length;

  const extraOnModel = ZONE_META.filter((zone) => {
    if (diagram.hotspots.some((spot) => spot.zone === zone.id)) return false;
    if (zone.id === "combo") return false;
    if (isForeignZone(zone.id, diagram)) return false;
    const count = rowsByZone.get(zone.id)?.length ?? 0;
    return count > 0 || diagram.zones.includes(zone.id);
  });

  const modeLights = modeNumber === null ? null : modeLightsFor(modeNumber);

  const mappedOnChannels = (channels: readonly string[]): string[] => {
    if (channels.length === 0) return [];
    const labels: string[] = [];
    for (const row of rows) {
      const inputs = row.cells.slice(2, 10).map((value) => value.trim());
      if (!inputs.some((input) => channels.includes(input))) continue;
      const name = row.cells[11]?.trim() || row.cells[0]?.trim();
      if (name) labels.push(name);
    }
    return labels;
  };

  const selectZone = (zone: ZoneMeta): void => {
    onSelectZone(zone.id);
    const first = rowsByZone.get(zone.id)?.[0];
    if (first !== undefined) onSelectRow(first.row);
  };

  const selectBackSocket = (zone: ZoneId, seed: string | null): void => {
    onSelectZone(zone);
    if (seed === null) return;
    const match = rows.find((row) =>
      row.cells.slice(2, 10).some((cell) => cell.trim() === seed),
    );
    if (match !== undefined) onSelectRow(match.row);
  };

  const selectCombo = (input: string): void => {
    setPickedCombo(input);
    setCombos(true);
    onSelectZone("combo");
    const prefix = comboPrefix(input);
    const comboRows = rowsByZone.get("combo") ?? [];
    const first = comboRows.find((row) =>
      row.cells.slice(2, 10).some((cell) => comboPrefix(cell.trim()) === prefix),
    );
    if (first !== undefined) onSelectRow(first.row);
  };

  const onHotspotKeyDown = (event: KeyboardEvent<HTMLButtonElement>, index: number): void => {
    if (stageSpotCount === 0) return;
    let next = index;
    if (event.key === "ArrowRight" || event.key === "ArrowDown") next = (index + 1) % stageSpotCount;
    else if (event.key === "ArrowLeft" || event.key === "ArrowUp") {
      next = (index - 1 + stageSpotCount) % stageSpotCount;
    } else return;
    event.preventDefault();
    setFocusedZone(next);
    hotspotRefs.current[next]?.focus();
  };

  const togglePractice = (): void => {
    setPractice((value) => !value);
  };

  const toggleCombos = (): void => {
    setCombos((value) => {
      const next = !value;
      if (!next) {
        setPickedCombo(null);
        if (selectedZone === "combo") onSelectZone("mp_left");
      }
      return next;
    });
    setFocusedZone(0);
  };

  const showFront = (): void => {
    onSelectZone("mp_center");
    setFocusedZone(0);
  };

  const joystickBody = () => {
    const summary = joystickSummary(rows);
    if (summary.isRecognized && summary.roleKey !== null) {
      return (
        <div className="joystick-summary">
          <span>{t("Main_Movement")}</span>
          {summary.roleToken !== "" ? (
            <OutputPrompt token={summary.roleToken} label={t(summary.roleKey)} size={28} />
          ) : null}
          <strong>{t(summary.roleKey)}</strong>
          {summary.extraActionCount > 0 ? (
            <span className="muted">{t("Main_ExtraActions", [summary.extraActionCount])}</span>
          ) : null}
        </div>
      );
    }
    const count = summary.actionCount;
    return (
      <div className="joystick-summary">
        <span className="muted">
          {count === 0 ? t("Main_NotMapped") : plural("Count_Action", count, [count])}
        </span>
        {count > 0 ? <span>{t("Main_ViewDetails")}</span> : null}
      </div>
    );
  };

  const foreignLabel = (zoneId: ZoneId) => {
    if (!isForeignZone(zoneId, diagram)) return null;
    return <span className="muted">{t("Main_NotOnModel")}</span>;
  };

  const calloutBody = (zone: ZoneMeta, zoneRows: readonly BindingCells[]) => {
    if (MOUTHPIECE_ZONES.has(zone.id)) {
      return <GestureTable rows={mouthpiece(rows, zone.id)} liveRows={liveRows} />;
    }
    if (zone.id === "joystick") return joystickBody();
    const count = zoneRows.length;
    return (
      <>
        <span className={count === 0 ? "muted" : "accent-count"}>
          {count === 0 ? t("Main_NotMapped") : plural("Count_Mapping", count, [count])}
        </span>
        {foreignLabel(zone.id)}
      </>
    );
  };

  const comboCalloutBody = (input: string) => (
    <GestureTable rows={mouthpiece(rows, "combo", comboPrefix(input))} liveRows={liveRows} />
  );

  if (view === "parts") {
    return (
      <section className="quadstick-visualizer parts-view" aria-labelledby="quadstick-visualizer-title">
        <header className="visualizer-header">
          <div>
            <h2 id="quadstick-visualizer-title">{t("Main_Parts")}</h2>
            <p>{modeNumber === null ? modeName : `${String(modeNumber)} · ${modeName}`}</p>
          </div>
        </header>
        <div className="parts-rail" aria-label={t("Main_Parts")}>
          {ZONE_META.map((zone) => {
            const zoneRows = rowsByZone.get(zone.id) ?? [];
            if (zoneRows.length === 0 && !diagram.zones.includes(zone.id)) return null;
            const selected = selectedZones.has(zone.id);
            const foreign = isForeignZone(zone.id, diagram);
            return (
              <button
                key={zone.id}
                type="button"
                className={selected ? "zone-rail-row selected" : "zone-rail-row"}
                aria-pressed={selected}
                style={foreign ? { opacity: 0.5 } : undefined}
                onClick={() => selectZone(zone)}
              >
                <strong>{t(zone.titleKey)}</strong>
                <span>
                  {zoneRows.length === 0
                    ? t("Main_NotMapped")
                    : plural("Count_Mapping", zoneRows.length, [zoneRows.length])}
                </span>
                {foreign ? <span className="muted">{t("Main_NotOnModel")}</span> : null}
              </button>
            );
          })}
        </div>
      </section>
    );
  }

  if (view === "rows") {
    return (
      <section className="quadstick-visualizer rows-view" aria-labelledby="quadstick-visualizer-title">
        <header className="visualizer-header">
          <div>
            <h2 id="quadstick-visualizer-title">{t("Shell_Rows")}</h2>
            <p>{modeNumber === null ? modeName : `${String(modeNumber)} · ${modeName}`}</p>
          </div>
        </header>
        <div className="binding-header" aria-hidden="true">
          <span className="tint-swatch tint-output">{t("Main_OutputGameButton")}</span>
          <span className="tint-swatch tint-function">{t("Main_FunctionBehavior")}</span>
          <span className="tint-swatch tint-input">{t("Main_InputsSipsPuffsJoystick")}</span>
        </div>
        <ul className="rows-list">
          {rows.map((row) => {
            const zone = zonesForRow(row)[0] ?? "unset";
            return (
              <li key={row.row}>
                <button
                  type="button"
                  className={selectedRow === row.row ? "row-chip selected" : "row-chip"}
                  data-binding-row={row.row}
                  data-testid={`binding-row-${String(row.row)}`}
                  aria-pressed={selectedRow === row.row}
                  onClick={() => {
                    onSelectZone(zone);
                    onSelectRow(row.row);
                  }}
                >
                  <span className="pill tint-output">
                    {row.cells[11]?.trim() || row.cells[0]?.trim() || t("Main_NothingYet")}
                  </span>
                  <span className="pill tint-function">
                    {row.cells[1]?.trim() && row.cells[1].trim().toLowerCase() !== "normal"
                      ? row.cells[1].trim()
                      : t("Main_NothingYet")}
                  </span>
                  <span className="pill tint-input">
                    {row.cells
                      .slice(2, 10)
                      .map((value) => value.trim())
                      .filter(Boolean)
                      .join(" · ") || t("Main_NoInput")}
                  </span>
                </button>
              </li>
            );
          })}
        </ul>
      </section>
    );
  }

  const renderModeLights = () => {
    if (modeNumber === null || diagram.lights === null || modeLights === null) return null;
    const lightRow = diagram.lights;
    const elements = [];
    for (let index = 0; index < modeLights.length; index += 1) {
      const light = modeLights[index]!;
      if (light === "off") continue;
      const at = onPhoto(diagram, lightX(lightRow, index), lightRow.y);
      const pointX = diagram.photoX + at.x;
      const pointY = photoY + at.y;
      const colour = ledCss(light);
      for (const [size, opacity] of [
        [26, 0.3],
        [13, 1],
      ] as const) {
        elements.push(
          <span
            key={`${String(index)}-${String(size)}`}
            className="mode-light"
            aria-hidden="true"
            style={{
              left: `${String((pointX / STAGE_W) * 100)}%`,
              top: `${String((pointY / stageH) * 100)}%`,
              width: `${String((size / STAGE_W) * 100)}%`,
              height: `${String((size / stageH) * 100)}%`,
              background: colour,
              opacity,
            }}
          />,
        );
      }
    }
    return (
      <>
        {elements}
        <output className="visually-hidden">
          {t("Main_DeviceShowsModeLightsDescribeLit", [describeModeLights(modeLights)])}
        </output>
      </>
    );
  };

  const renderComboHighlight = () => {
    if (!showCombos || pickedCombo === null) return null;
    const points = comboPoints(diagram, pickedCombo);
    if (points.length === 0) return null;
    const stagePoints = points.map((point) => {
      const at = onPhoto(diagram, point.x, point.y);
      return { x: diagram.photoX + at.x, y: photoY + at.y };
    });
    return (
      <div className="combo-highlight" aria-hidden="true">
        <svg
          className="combo-links"
          viewBox={`0 0 ${String(STAGE_W)} ${String(stageH)}`}
          preserveAspectRatio="none"
        >
          {stagePoints.slice(1).map((point, index) => (
            <line
              key={String(index)}
              className="combo-link"
              x1={stagePoints[index]!.x}
              y1={stagePoints[index]!.y}
              x2={point.x}
              y2={point.y}
            />
          ))}
        </svg>
        {stagePoints.map((point, index) => (
          <span
            key={String(index)}
            className="combo-rings"
            style={{
              left: `${String((point.x / STAGE_W) * 100)}%`,
              top: `${String((point.y / stageH) * 100)}%`,
            }}
          >
            <span className="combo-ring ring-halo" />
            <span className="combo-ring ring-focus" />
            <span className="combo-ring ring-accent" />
          </span>
        ))}
      </div>
    );
  };

  const renderHotspot = (spot: Hotspot, index: number, comboMode: boolean) => {
    const at = onPhoto(diagram, spot.pointX, spot.pointY);
    const pointX = diagram.photoX + at.x;
    const pointY = photoY + at.y;
    const calloutHeight = spot.bottom ? SMALL_PILL_H : PILL_H;
    const labelTop = spot.bottom ? bottomY : calloutBottom - calloutHeight;
    const lineY = spot.bottom ? bottomY : calloutBottom;

    if (comboMode) {
      const input = spot.zone;
      const prefix = comboPrefix(input);
      const selected = pickedCombo !== null && comboPrefix(pickedCombo) === prefix;
      const faded = pickedCombo !== null && !selected;
      const summaries = mouthpiece(rows, "combo", prefix);
      const active = gestureRowsLive(summaries, liveRows);
      const name = comboAccessibleName(input, rows, t, plural);

      return (
        <div className={faded ? "hotspot-group faded" : "hotspot-group"} key={input}>
          <span
            className="hotspot-marker"
            aria-hidden="true"
            style={{
              left: `${String((pointX / STAGE_W) * 100)}%`,
              top: `${String((pointY / stageH) * 100)}%`,
            }}
          />
          <svg
            className="hotspot-line"
            aria-hidden="true"
            viewBox={`0 0 ${String(STAGE_W)} ${String(stageH)}`}
            preserveAspectRatio="none"
          >
            <line
              className="leader-under"
              x1={spot.labelX + PILL_W / 2}
              y1={lineY}
              x2={pointX}
              y2={pointY}
            />
            <line
              className="leader-over"
              x1={spot.labelX + PILL_W / 2}
              y1={lineY}
              x2={pointX}
              y2={pointY}
            />
          </svg>
          <button
            ref={(element) => {
              hotspotRefs.current[index] = element;
            }}
            type="button"
            className={selected ? "zone-callout selected" : "zone-callout"}
            aria-label={name}
            aria-pressed={selected}
            data-live={active ? "true" : undefined}
            tabIndex={focusedZone === index ? 0 : -1}
            style={{
              left: `${String((spot.labelX / STAGE_W) * 100)}%`,
              top: `${String((labelTop / stageH) * 100)}%`,
              width: `${String((PILL_W / STAGE_W) * 100)}%`,
              minHeight: `${String((calloutHeight / stageH) * 100)}%`,
            }}
            onFocus={() => setFocusedZone(index)}
            onKeyDown={(event) => onHotspotKeyDown(event, index)}
            onClick={() => selectCombo(input)}
          >
            <strong className="zone-callout-title">{comboPairTitle(input, t)}</strong>
            {comboCalloutBody(input)}
          </button>
        </div>
      );
    }

    const zone = photoZones.find((candidate) => candidate.id === spot.zone);
    if (zone === undefined) return <div key={spot.zone} />;
    const zoneRows = rowsByZone.get(zone.id) ?? [];
    const selected = selectedZones.has(zone.id);
    const summaries = MOUTHPIECE_ZONES.has(zone.id) ? mouthpiece(rows, zone.id) : [];
    const active =
      (zone.id === "joystick" && liveJoystickActive(live)) ||
      (MOUTHPIECE_ZONES.has(zone.id)
        ? gestureRowsLive(summaries, liveRows)
        : zoneRowsLive(zoneRows, liveRows));
    const name = zoneAccessibleName(zone, rows, t, plural);

    return (
      <div className="hotspot-group" key={zone.id}>
        <span
          className="hotspot-marker"
          aria-hidden="true"
          data-active={zone.id === "joystick" && liveJoystickActive(live) ? "true" : undefined}
          style={{
            left: `${String((pointX / STAGE_W) * 100)}%`,
            top: `${String((pointY / stageH) * 100)}%`,
          }}
        />
        <svg
          className="hotspot-line"
          aria-hidden="true"
          viewBox={`0 0 ${String(STAGE_W)} ${String(stageH)}`}
          preserveAspectRatio="none"
        >
          <line className="leader-under" x1={spot.labelX + PILL_W / 2} y1={lineY} x2={pointX} y2={pointY} />
          <line className="leader-over" x1={spot.labelX + PILL_W / 2} y1={lineY} x2={pointX} y2={pointY} />
        </svg>
        <button
          ref={(element) => {
            hotspotRefs.current[index] = element;
          }}
          type="button"
          className={selected ? "zone-callout selected" : "zone-callout"}
          aria-label={name}
          aria-pressed={selected}
          data-live-active={active ? "true" : undefined}
          data-live={active ? "true" : undefined}
          tabIndex={focusedZone === index ? 0 : -1}
          style={{
            left: `${String((spot.labelX / STAGE_W) * 100)}%`,
            top: `${String((labelTop / stageH) * 100)}%`,
            width: `${String((PILL_W / STAGE_W) * 100)}%`,
            minHeight: `${String((calloutHeight / stageH) * 100)}%`,
          }}
          onFocus={() => setFocusedZone(index)}
          onKeyDown={(event) => onHotspotKeyDown(event, index)}
          onClick={() => selectZone(zone)}
        >
          <strong className="zone-callout-title">{t(zone.shortKey)}</strong>
          {calloutBody(zone, zoneRows)}
        </button>
      </div>
    );
  };

  const renderBackPanel = () => (
    <div className="back-panel">
      <div
        className="visualizer-stage back-stage"
        style={{ aspectRatio: `${String(BACK_STAGE_W)} / ${String(BACK_STAGE_H)}` }}
        dir="ltr"
      >
        <img
          className="quadstick-photo back-photo"
          src={backPhoto}
          alt=""
          aria-hidden="true"
          style={{
            left: `${String((BACK_PHOTO_X / BACK_STAGE_W) * 100)}%`,
            top: `${String((BACK_PHOTO_Y / BACK_STAGE_H) * 100)}%`,
            width: `${String((BACK_PHOTO_W / BACK_STAGE_W) * 100)}%`,
            height: `${String((BACK_PHOTO_H / BACK_STAGE_H) * 100)}%`,
          }}
        />
        {BACK_SOCKETS.map((socket) => {
          const pointX = BACK_PHOTO_X + socket.fx * BACK_PHOTO_W;
          const pointY = BACK_PHOTO_Y + socket.fy * BACK_PHOTO_H;
          const labelX = socket.left ? 0 : BACK_STAGE_W - BACK_PILL_W;
          const anchorX = socket.left ? BACK_PILL_W : labelX;
          const mapped = mappedOnChannels(socket.channels);
          const selected = selectedZone === socket.zone;
          return (
            <div className="hotspot-group" key={socket.nameKey}>
              <span
                className="hotspot-marker"
                aria-hidden="true"
                style={{
                  left: `${String((pointX / BACK_STAGE_W) * 100)}%`,
                  top: `${String((pointY / BACK_STAGE_H) * 100)}%`,
                }}
              />
              <svg
                className="hotspot-line"
                aria-hidden="true"
                viewBox={`0 0 ${String(BACK_STAGE_W)} ${String(BACK_STAGE_H)}`}
                preserveAspectRatio="none"
              >
                <line
                  className="leader-under"
                  x1={anchorX}
                  y1={socket.labelY + BACK_PILL_H / 2}
                  x2={pointX}
                  y2={pointY}
                />
                <line
                  className="leader-over"
                  x1={anchorX}
                  y1={socket.labelY + BACK_PILL_H / 2}
                  x2={pointX}
                  y2={pointY}
                />
              </svg>
              <button
                type="button"
                className={selected ? "zone-callout back-socket selected" : "zone-callout back-socket"}
                aria-pressed={selected}
                aria-label={`${t(socket.nameKey)}. ${t(socket.detailKey)}`}
                style={{
                  left: `${String((labelX / BACK_STAGE_W) * 100)}%`,
                  top: `${String((socket.labelY / BACK_STAGE_H) * 100)}%`,
                  width: `${String((BACK_PILL_W / BACK_STAGE_W) * 100)}%`,
                  minHeight: `${String((BACK_PILL_H / BACK_STAGE_H) * 100)}%`,
                }}
                onClick={() => selectBackSocket(socket.zone, socket.seed)}
              >
                <strong className="zone-callout-title">{t(socket.nameKey)}</strong>
                <span className="muted">{t(socket.detailKey)}</span>
                {mapped.length > 0 ? (
                  <span className="back-mapped">{mapped.slice(0, 3).join(" · ")}</span>
                ) : null}
              </button>
            </div>
          );
        })}
      </div>
      <ul className="back-guide">
        {BACK_SOCKETS.filter((socket) => socket.zone === "jacks").map((socket) => (
          <li key={socket.guideKey}>{t(socket.guideKey)}</li>
        ))}
      </ul>
    </div>
  );

  const offModelTotal = foreignZones.reduce(
    (sum, zone) => sum + (rowsByZone.get(zone.id)?.length ?? 0),
    0,
  );

  return (
    <section className="quadstick-visualizer" aria-labelledby="quadstick-visualizer-title">
      <header className="visualizer-header">
        <div>
          <h2 id="quadstick-visualizer-title">{t("Tour_ThisIsYourQuadStickEach")}</h2>
          <p>{modeNumber === null ? modeName : `${String(modeNumber)} · ${modeName}`}</p>
        </div>
        <div className="visualizer-header-actions">
          {showBack ? (
            <button type="button" className="combo-toggle" onClick={showFront}>
              {t("Main_MainControls")}
            </button>
          ) : null}
          {!showBack && diagram.zones.includes("combo") ? (
            <button
              type="button"
              className={showCombos ? "combo-toggle active" : "combo-toggle"}
              aria-pressed={showCombos}
              onClick={toggleCombos}
            >
              {t("Main_Combos")}
            </button>
          ) : null}
          <button
            type="button"
            className={practice ? "practice-toggle active" : "practice-toggle"}
            aria-pressed={practice}
            onClick={togglePractice}
          >
            {practice ? t("Main_UsingDeviceView") : t("DevicePage_JoystickTravel")}
          </button>
        </div>
      </header>

      {foreignZones.length > 0 ? (
        <p className="model-mismatch" role="status">
          {t("Main_ThisProfileMapsPartsYour", [
            plural("Count_Part", foreignZones.length, [foreignZones.length]),
            MODEL_SHORT[model],
          ])}
        </p>
      ) : null}

      <div className="visualizer-stage-scroll">
        {showBack ? (
          renderBackPanel()
        ) : (
          <div
            className="visualizer-stage"
            style={{ aspectRatio: `${String(STAGE_W)} / ${String(stageH)}` }}
            dir="ltr"
          >
            <div
              className="photo-frame"
              aria-hidden="true"
              style={{
                position: "absolute",
                overflow: "hidden",
                left: `${String((diagram.photoX / STAGE_W) * 100)}%`,
                top: `${String((photoY / stageH) * 100)}%`,
                width: `${String((diagram.photoW / STAGE_W) * 100)}%`,
                height: `${String((photoH / stageH) * 100)}%`,
              }}
            >
              <img
                className="quadstick-photo"
                src={photoFor(model)}
                alt=""
                aria-hidden="true"
                style={{
                  position: "absolute",
                  left: `${String((-source.x / source.w) * 100)}%`,
                  top: `${String((-source.y / source.h) * 100)}%`,
                  width: `${String((100 / source.w) * 100)}%`,
                  height: `${String((100 / source.h) * 100)}%`,
                  objectFit: "fill",
                }}
              />
            </div>

            {renderModeLights()}
            {renderComboHighlight()}

            {practice && live?.status.kind === "reading"
              ? (() => {
                  const joy = diagram.hotspots.find((spot) => spot.zone === "joystick");
                  if (joy === undefined) return null;
                  const at = onPhoto(diagram, joy.pointX, joy.pointY);
                  const x = diagram.photoX + at.x + live.status.motion.x * 30;
                  const y = photoY + at.y + live.status.motion.y * 30;
                  return (
                    <span
                      className="live-stick-dot"
                      aria-hidden="true"
                      style={{
                        left: `${String((x / STAGE_W) * 100)}%`,
                        top: `${String((y / stageH) * 100)}%`,
                      }}
                    />
                  );
                })()
              : null}

            {stageSpots.map((spot, index) => renderHotspot(spot, index, showCombos))}
          </div>
        )}
      </div>

      {extraOnModel.length > 0 || foreignZones.length > 0 ? (
        <div className="visualizer-extra-zones" aria-label={t("Main_Parts")}>
          {!showBack ? (
            <button
              type="button"
              className="zone main-controls"
              aria-label={t("Main_MainControlsCount", [
                plural(
                  "Count_Mapping",
                  rows.filter((row) =>
                    zonesForRow(row).some((zone) => diagram.zones.includes(zone) && zone !== "combo"),
                  ).length,
                  [
                    rows.filter((row) =>
                      zonesForRow(row).some((zone) => diagram.zones.includes(zone) && zone !== "combo"),
                    ).length,
                  ],
                ),
              ])}
              onClick={showFront}
            >
              <strong>{t("Main_MainControls")}</strong>
            </button>
          ) : null}
          {extraOnModel.map((zone) => {
            const count = rowsByZone.get(zone.id)?.length ?? 0;
            return (
              <button
                key={zone.id}
                type="button"
                className={selectedZones.has(zone.id) ? "zone selected" : "zone"}
                onClick={() => selectZone(zone)}
              >
                <strong>{t(zone.titleKey)}</strong>
                <span>
                  {count === 0 ? t("Main_NotMapped") : plural("Count_Mapping", count, [count])}
                </span>
              </button>
            );
          })}
          {foreignZones.length > 0 ? (
            <details className="off-model-card">
              <summary
                aria-label={t("Main_NotOnYourModelNameParts", [
                  t("Main_NotOnYourModelName", [MODEL_SHORT[model]]),
                  plural("Count_Part", foreignZones.length, [foreignZones.length]),
                  plural("Count_Mapping", offModelTotal, [offModelTotal]),
                ])}
              >
                <strong>{t("Main_NotOnYourModelName", [MODEL_SHORT[model]])}</strong>
                <span className="muted">
                  {plural("Count_Part", foreignZones.length, [foreignZones.length])}
                </span>
                <span className="accent-count">
                  {plural("Count_Mapping", offModelTotal, [offModelTotal])}
                </span>
              </summary>
              <p className="muted">{t("Main_TheseRowsAreKeptInThe")}</p>
              {foreignZones.map((zone) => {
                const count = rowsByZone.get(zone.id)?.length ?? 0;
                return (
                  <button
                    key={zone.id}
                    type="button"
                    className={selectedZones.has(zone.id) ? "zone selected" : "zone"}
                    onClick={() => selectZone(zone)}
                  >
                    <strong>{t(zone.titleKey)}</strong>
                    <span>{plural("Count_Mapping", count, [count])}</span>
                    <span className="muted">{t("Main_NotOnModel")}</span>
                  </button>
                );
              })}
            </details>
          ) : null}
        </div>
      ) : null}

      <output className="practice-status" aria-live="polite">
        {live?.status.kind === "reading" ? liveText(live, t) : t("Main_WhatALitRowMeans")}
      </output>
    </section>
  );
}
