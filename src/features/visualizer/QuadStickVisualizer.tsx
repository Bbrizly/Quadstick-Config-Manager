import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from "react";

import fpsPhoto from "../../QuadStick.App/Assets/QuadStickFPS.png";
import { useI18n, type MessageKey } from "../../i18n";
import type { LiveSnapshot, QcmClient } from "../../platform";
import {
  FPS_DIAGRAM,
  PILL_H,
  PILL_W,
  SMALL_PILL_H,
  STAGE_W,
  TOP_CALLOUT_GAP,
  deviceBottomLabelY,
  devicePhotoY,
  deviceStageHeight,
  onPhoto,
  photoHeight,
} from "./deviceDiagram";
import {
  joystickSummary,
  mouthpiece,
  summaryActionText,
  zonesForRow,
  type BindingCells,
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
}: QuadStickVisualizerProps) {
  const { t, plural } = useI18n();
  const [practice, setPractice] = useState(false);
  const [live, setLive] = useState<LiveSnapshot | null>(null);
  const [focusedZone, setFocusedZone] = useState(0);
  const hotspotRefs = useRef<Array<HTMLButtonElement | null>>([]);
  const diagram = FPS_DIAGRAM;

  const rowsByZone = useMemo(() => {
    const map = new Map<ZoneId, BindingCells[]>();
    for (const zone of ZONE_META) map.set(zone.id, []);
    for (const row of rows) {
      for (const zone of zonesForRow(row)) map.get(zone)?.push(row);
    }
    return map;
  }, [rows]);

  const selectedZones = useMemo(() => {
    if (selectedZone !== null) return new Set<ZoneId>([selectedZone]);
    const row = selectedRow === null ? undefined : rows.find((candidate) => candidate.row === selectedRow);
    return new Set(row === undefined ? [] : zonesForRow(row));
  }, [rows, selectedRow, selectedZone]);

  useEffect(() => {
    if (!practice) return;
    let disposed = false;
    let subscription: { dispose(): void } | null = null;
    void client
      .startLiveInput((frame) => {
        if (!disposed) setLive(frame);
      })
      .then((value) => {
        if (disposed) value.dispose();
        else subscription = value;
      })
      .catch(() => {
        if (!disposed) setLive(null);
      });
    return () => {
      disposed = true;
      subscription?.dispose();
    };
  }, [client, practice]);

  const photoY = devicePhotoY(diagram);
  const photoH = photoHeight(diagram);
  const stageH = deviceStageHeight(diagram);
  const bottomY = deviceBottomLabelY(diagram);
  const calloutBottom = photoY - TOP_CALLOUT_GAP;
  const photoZones = ZONE_META.filter((zone) => diagram.hotspots.some((spot) => spot.zone === zone.id));
  const extraZones = ZONE_META.filter(
    (zone) =>
      !diagram.hotspots.some((spot) => spot.zone === zone.id) &&
      (rowsByZone.get(zone.id)?.length ?? 0) > 0,
  );

  const selectZone = (zone: ZoneDefinitionOrMeta): void => {
    onSelectZone(zone.id);
    const first = rowsByZone.get(zone.id)?.[0];
    if (first !== undefined) onSelectRow(first.row);
  };

  type ZoneDefinitionOrMeta = ZoneMeta;

  const onHotspotKeyDown = (event: KeyboardEvent<HTMLButtonElement>, index: number): void => {
    let next = index;
    if (event.key === "ArrowRight" || event.key === "ArrowDown") next = (index + 1) % photoZones.length;
    else if (event.key === "ArrowLeft" || event.key === "ArrowUp") {
      next = (index - 1 + photoZones.length) % photoZones.length;
    } else return;
    event.preventDefault();
    setFocusedZone(next);
    hotspotRefs.current[next]?.focus();
  };

  const togglePractice = (): void => {
    if (practice) setLive(null);
    setPractice((value) => !value);
  };

  const joystickBody = () => {
    const summary = joystickSummary(rows);
    if (summary.isRecognized && summary.roleKey !== null) {
      return (
        <div className="joystick-summary">
          <span>{t("Main_Movement")}</span>
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

  const calloutBody = (zone: ZoneMeta, zoneRows: readonly BindingCells[]) => {
    if (MOUTHPIECE_ZONES.has(zone.id)) {
      return <GestureTable rows={mouthpiece(rows, zone.id)} />;
    }
    if (zone.id === "joystick") return joystickBody();
    const count = zoneRows.length;
    return (
      <span className={count === 0 ? "muted" : "accent-count"}>
        {count === 0 ? t("Main_NotMapped") : plural("Count_Mapping", count, [count])}
      </span>
    );
  };

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
            return (
              <button
                key={zone.id}
                type="button"
                className={selected ? "zone-rail-row selected" : "zone-rail-row"}
                aria-pressed={selected}
                onClick={() => selectZone(zone)}
              >
                <strong>{t(zone.titleKey)}</strong>
                <span>
                  {zoneRows.length === 0
                    ? t("Main_NotMapped")
                    : plural("Count_Mapping", zoneRows.length, [zoneRows.length])}
                </span>
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
                {/* Sentence cards live in the detail panel; rows view shows tinted chips. */}
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
                  <span className="pill tint-function">{row.cells[1]?.trim() || "normal"}</span>
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

  return (
    <section className="quadstick-visualizer" aria-labelledby="quadstick-visualizer-title">
      <header className="visualizer-header">
        <div>
          <h2 id="quadstick-visualizer-title">{t("Tour_ThisIsYourQuadStickEach")}</h2>
          <p>{modeNumber === null ? modeName : `${String(modeNumber)} · ${modeName}`}</p>
        </div>
        <button
          type="button"
          className={practice ? "practice-toggle active" : "practice-toggle"}
          aria-pressed={practice}
          onClick={togglePractice}
        >
          {practice ? t("Main_UsingDeviceView") : t("DevicePage_JoystickTravel")}
        </button>
      </header>

      <div className="visualizer-stage-scroll">
        <div className="visualizer-stage" style={{ width: STAGE_W, height: stageH }} dir="ltr">
          <img
            className="quadstick-photo"
            src={fpsPhoto}
            alt=""
            aria-hidden="true"
            style={{
              left: diagram.photoX,
              top: photoY,
              width: diagram.photoW,
              height: photoH,
            }}
          />
          {practice && live?.status.kind === "reading"
            ? (() => {
                const joy = diagram.hotspots.find((spot) => spot.zone === "joystick");
                if (joy === undefined) return null;
                const at = onPhoto(diagram, joy.pointX, joy.pointY);
                return (
                  <span
                    className="live-stick-dot"
                    aria-hidden="true"
                    style={{
                      left: diagram.photoX + at.x + live.status.motion.x * 30,
                      top: photoY + at.y + live.status.motion.y * 30,
                    }}
                  />
                );
              })()
            : null}

          {photoZones.map((zone, index) => {
            const spot = diagram.hotspots.find((candidate) => candidate.zone === zone.id)!;
            const at = onPhoto(diagram, spot.pointX, spot.pointY);
            const pointX = diagram.photoX + at.x;
            const pointY = photoY + at.y;
            const calloutHeight = spot.bottom ? SMALL_PILL_H : PILL_H;
            const labelTop = spot.bottom ? bottomY : calloutBottom - calloutHeight;
            const lineY = spot.bottom ? bottomY : calloutBottom;
            const zoneRows = rowsByZone.get(zone.id) ?? [];
            const selected = selectedZones.has(zone.id);
            const active = zone.id === "joystick" && practice && liveJoystickActive(live);
            const name = zoneAccessibleName(zone, rows, t, plural);

            return (
              <div className="hotspot-group" key={zone.id}>
                <span
                  className="hotspot-marker"
                  aria-hidden="true"
                  data-active={active ? "true" : undefined}
                  style={{ left: pointX, top: pointY }}
                />
                <svg
                  className="hotspot-line"
                  aria-hidden="true"
                  viewBox={`0 0 ${String(STAGE_W)} ${String(stageH)}`}
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
                  data-live-active={active ? "true" : undefined}
                  tabIndex={focusedZone === index ? 0 : -1}
                  style={{
                    left: spot.labelX,
                    top: labelTop,
                    width: PILL_W,
                    minHeight: calloutHeight,
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
          })}
        </div>
      </div>

      {extraZones.length > 0 ? (
        <div className="visualizer-extra-zones" aria-label={t("Main_Parts")}>
          {extraZones.map((zone) => {
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
              </button>
            );
          })}
        </div>
      ) : null}

      <output className="practice-status" aria-live="polite">
        {practice ? liveText(live, t) : t("Main_WhatALitRowMeans")}
      </output>
    </section>
  );
}
