import { createPortal } from "react-dom";
import { useEffect, useLayoutEffect, useMemo, useRef, useState, type KeyboardEvent, type ReactNode } from "react";

import backPhoto from "../../QuadStick.App/Assets/QuadStickBack.png";
import { useI18n, type MessageKey } from "../../i18n";
import type { LiveSnapshot, QcmClient } from "../../platform";
import {
  COMBO_PLACES,
  PILL_H,
  PILL_W,
  SMALL_PILL_H,
  STAGE_W,
  TOP_CALLOUT_GAP,
  comboHotspots,
  comboPoints,
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
import { useLiveInput } from "../live/useLiveInput";
import { GestureTable } from "./GestureTable";
import { comboPairTitle, comboPrefix, firstMappedCombo } from "./parts";
import { photoFor } from "./photos";
import { stageRoom, verticalScroller } from "./stageFit";
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
  /** `null` is Main controls: the front photo with nothing picked. */
  readonly onSelectZone: (zone: ZoneId | null) => void;
  readonly model?: QsModel;
  readonly onLiveRows?: (rows: ReadonlySet<number>) => void;
  /** The socket or hole pairing picked on the picture, when the parent narrows its list to it. */
  readonly pickedInput?: string | null;
  readonly onPickInput?: (input: string | null) => void;
  /**
   * Where the parts with no place on the photo are listed (Avalonia's
   * `ZoneList`, in the sidebar). Without one they sit under the picture.
   */
  readonly zoneListSlot?: HTMLElement | null;
  /** Whether the live reader hears a QuadStick, on the found and lost edges only. */
  readonly onLiveHeard?: (heard: boolean) => void;
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

const NO_OUTPUTS: ReadonlySet<string> = new Set();

const MOUTHPIECE_ZONES = new Set<ZoneId>(["mp_left", "mp_center", "mp_right", "side", "lip"]);

/**
 * Avalonia draws the stage at its design size inside a Viewbox, so callouts,
 * type and leader lines shrink with the picture. This does the same: the
 * children lay out on a fixed design-size layer that is scaled to the width
 * it gets. The scale is measured before paint and again only on a resize, so
 * there is no layout shift on load and nothing runs while idle.
 */
function StageViewbox({
  width,
  height,
  className,
  children,
}: {
  readonly width: number;
  readonly height: number;
  readonly className: string;
  readonly children: ReactNode;
}) {
  const outer = useRef<HTMLDivElement>(null);
  const [scale, setScale] = useState(1);
  useLayoutEffect(() => {
    const element = outer.current;
    if (element === null) return undefined;
    // Fit the height too, as a Uniform Viewbox does: a stage sized by width
    // alone hid the bottom callouts under a scroll with no bar on macOS. The
    // 626px CSS floor (scale 0.69, 11px callouts) still wins; below it the canvas scrolls.
    const canvas = element.closest<HTMLElement>(".device-canvas");
    const fit = () => {
      // The box whose height to fit is whatever scrolls the stage: the canvas
      // beside the inspector, or the column it shares once stacked (B-096).
      // The aim is the whole stage in view once its canvas is scrolled to the top.
      const box = canvas === null ? null : verticalScroller(element);
      if (canvas !== null && box !== null) {
        const canvasRect = canvas.getBoundingClientRect();
        // A problems bar pinned over the bottom of the scroller hides that much.
        const dock = box.querySelector<HTMLElement>(".problems-dock");
        const room = stageRoom({
          boxHeight: box.clientHeight,
          rectDelta: element.getBoundingClientRect().top - canvasRect.top,
          zoom: canvas.offsetWidth > 0 ? canvasRect.width / canvas.offsetWidth : 1,
          scrollTop: box === canvas ? canvas.scrollTop : 0,
          paddingBottom: Number.parseFloat(getComputedStyle(canvas).paddingBottom) || 0,
          covered: dock !== null && getComputedStyle(dock).position === "sticky" ? dock.offsetHeight : 0,
        });
        element.style.maxWidth = room > 0 ? `${String(Math.min(width, (room * width) / height))}px` : "";
      }
      const room = element.clientWidth;
      if (room > 0) setScale(room / width);
    };
    fit();
    if (typeof ResizeObserver === "undefined") return undefined;
    const observer = new ResizeObserver(fit);
    observer.observe(element);
    if (canvas !== null) observer.observe(canvas);
    // Any of these may be the scroller, depending on the editor's width.
    for (const box of [canvas?.parentElement, element.closest(".editor-plate"), element.closest(".editor-workspace")]) {
      if (box instanceof HTMLElement) observer.observe(box);
    }
    return () => observer.disconnect();
  }, [width, height]);
  return (
    <div
      ref={outer}
      className={className}
      style={{ aspectRatio: `${String(width)} / ${String(height)}` }}
      dir="ltr"
    >
      <div
        className="stage-viewbox"
        style={{ width, height, transform: scale === 1 ? undefined : `scale(${String(scale)})` }}
      >
        {children}
      </div>
    </div>
  );
}

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
  pickedInput,
  onPickInput,
  zoneListSlot,
  onLiveHeard,
}: QuadStickVisualizerProps) {
  const { t, plural } = useI18n();
  const [chosenCombo, setPickedCombo] = useState<string | null>(null);
  const [live, setLive] = useState<LiveSnapshot | null>(null);
  const [liveOutputs, setLiveOutputs] = useState<ReadonlySet<string>>(new Set());
  const [focusedZone, setFocusedZone] = useState(0);
  const hotspotRefs = useRef<Array<HTMLButtonElement | null>>([]);
  const previousOutputs = useRef<ReadonlySet<string> | null>(null);
  const diagram = diagramFor(model);

  const showBack = selectedZone === "jacks" || selectedZone === "other";
  // Avalonia: the photo follows the part picked on the left. Hole combos are
  // drawn only while that row is the one picked.
  const showCombos = !showBack && selectedZone === "combo" && diagram.zones.includes("combo");

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

  const heard = live !== null && (live.status.kind === "reading" || live.status.kind === "xinputOnly");
  useEffect(() => {
    onLiveHeard?.(heard);
  }, [heard, onLiveHeard]);

  const windowActive = useLiveInput(client, (frame) => {
    setLive(frame);
    if (frame !== null && frame.status.kind === "reading") {
      const next = outputsFromMotion(frame.status.motion, previousOutputs.current);
      previousOutputs.current = next;
      setLiveOutputs(next);
    } else if (previousOutputs.current !== null || frame === null) {
      previousOutputs.current = null;
      setLiveOutputs(NO_OUTPUTS);
    }
  });


  // Avalonia auto-picks the first mapped pairing so hole rings appear immediately.
  // A parent that narrows its list owns the pick, so the rings and the list
  // can never name two different pairings.
  const chosen =
    pickedInput === undefined
      ? chosenCombo
      : pickedInput !== null && comboPrefix(pickedInput) !== ""
        ? pickedInput
        : null;
  const pickedCombo = useMemo(() => {
    if (!showCombos) return chosen;
    if (chosen !== null && comboPrefix(chosen) !== "") return chosen;
    return firstMappedCombo(rowsByZone.get("combo") ?? []);
  }, [showCombos, chosen, rowsByZone]);

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
    onPickInput?.(null);
    const first = rowsByZone.get(zone.id)?.[0];
    if (first !== undefined) onSelectRow(first.row);
  };

  const selectBackSocket = (zone: ZoneId, seed: string | null): void => {
    onSelectZone(zone);
    onPickInput?.(seed);
    if (seed === null) return;
    const match = rows.find((row) =>
      row.cells.slice(2, 10).some((cell) => cell.trim() === seed),
    );
    if (match !== undefined) onSelectRow(match.row);
  };

  const selectCombo = (input: string): void => {
    setPickedCombo(input);
    onSelectZone("combo");
    onPickInput?.(input);
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

  // Avalonia `MainControlsRow`: back to the front photo with nothing picked,
  // so the panel asks which part rather than guessing one.
  const showFront = (): void => {
    onSelectZone(null);
    onPickInput?.(null);
    setPickedCombo(null);
    setFocusedZone(0);
  };

  const joystickBody = () => {
    const summary = joystickSummary(rows);
    if (summary.isRecognized && summary.roleKey !== null) {
      return (
        <div className="joystick-summary">
          <span>{t("Main_Movement")}</span>
          {summary.roleToken !== "" ? (
            <OutputPrompt token={summary.roleToken} label={t(summary.roleKey)} size={30} />
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
    // Top callouts hang from their bottom edge, 10 units above the photo, so
    // a callout taller than the 116 Avalonia reserves grows up into the empty
    // band above rather than down over the top of the photo.
    const place = spot.bottom
      ? { top: `${String((bottomY / stageH) * 100)}%` }
      : { bottom: `${String(((stageH - calloutBottom) / stageH) * 100)}%` };
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
              ...place,
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
            ...place,
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
      <StageViewbox
        className="visualizer-stage back-stage"
        width={BACK_STAGE_W}
        height={BACK_STAGE_H}
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
      </StageViewbox>
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

  const frontCount = rows.filter((row) =>
    zonesForRow(row).some((zone) => diagram.hotspots.some((spot) => spot.zone === zone)),
  ).length;
  const frontSelected = selectedZone === null || diagram.hotspots.some((spot) => spot.zone === selectedZone);
  const countText = (count: number): string =>
    count === 0 ? t("Main_NotMapped") : plural("Count_Mapping", count, [count]);
  // Avalonia `FillZoneList`: Main controls, then every part with no marker
  // on the photo, then the parts this model has not got behind one card.
  const zoneList = (
    <div className="visualizer-extra-zones" role="group" aria-label={t("Main_Parts")}>
      <button
        type="button"
        className={frontSelected ? "zone main-controls selected" : "zone main-controls"}
        aria-pressed={frontSelected}
        aria-label={t("Main_MainControlsCount", [countText(frontCount)])}
        onClick={showFront}
      >
        <strong>{t("Main_MainControls")}</strong>
        <span className={frontCount === 0 ? "muted" : "accent-count"}>{countText(frontCount)}</span>
      </button>
      {[
        ...(diagram.zones.includes("combo") ? [ZONE_META.find((zone) => zone.id === "combo")!] : []),
        ...extraOnModel,
      ].map((zone) => {
        const count = rowsByZone.get(zone.id)?.length ?? 0;
        const selected = selectedZone === zone.id;
        return (
          <button
            key={zone.id}
            type="button"
            className={selected ? "zone selected" : "zone"}
            aria-pressed={selected}
            onClick={() => (zone.id === "combo" ? selectCombo(firstMappedCombo(rowsByZone.get("combo") ?? []) ?? COMBO_PLACES[0]!.input) : selectZone(zone))}
          >
            <strong>{t(zone.titleKey)}</strong>
            <span className={count === 0 ? "muted" : "accent-count"}>{countText(count)}</span>
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
            <span className="muted">{plural("Count_Part", foreignZones.length, [foreignZones.length])}</span>
            <span className="accent-count">{plural("Count_Mapping", offModelTotal, [offModelTotal])}</span>
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
  );



  return (
    <section
      className="quadstick-visualizer"
      aria-labelledby="quadstick-visualizer-title"
      data-window-active={windowActive ? undefined : "false"}
    >
      <h2 id="quadstick-visualizer-title" className="visually-hidden">
        {t("Tour_ThisIsYourQuadStickEach")} {modeNumber === null ? modeName : `${String(modeNumber)} · ${modeName}`}
      </h2>

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
          <StageViewbox className="visualizer-stage" width={STAGE_W} height={stageH}>
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
                  width: `${String(100 / source.w)}%`,
                  height: `${String(100 / source.h)}%`,
                  objectFit: "fill",
                }}
              />
            </div>

            {renderModeLights()}
            {renderComboHighlight()}

            {stageSpots.map((spot, index) => renderHotspot(spot, index, showCombos))}
          </StageViewbox>
        )}
      </div>

      {zoneListSlot === undefined ? zoneList : zoneListSlot === null ? null : createPortal(zoneList, zoneListSlot)}
    </section>
  );
}
