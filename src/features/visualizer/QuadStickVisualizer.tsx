import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from "react";

import quadStickPhoto from "../../QuadStick.App/Assets/QuadStick.png";
import { useI18n, type MessageKey } from "../../i18n";
import type { LiveSnapshot, QcmClient } from "../../platform";

export interface VisualizerBinding {
  readonly row: number;
  readonly cells: readonly string[];
}

interface QuadStickVisualizerProps {
  readonly client: QcmClient;
  readonly rows: readonly VisualizerBinding[];
  readonly selectedRow: number | null;
  readonly modeName: string;
  readonly modeNumber: number | null;
  readonly onSelectRow: (row: number) => void;
}

interface ZoneDefinition {
  readonly id: ZoneId;
  readonly titleKey: MessageKey;
  readonly shortKey: MessageKey;
  readonly labelX?: number;
  readonly labelY?: number;
  readonly pointX?: number;
  readonly pointY?: number;
}

type ZoneId =
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

// Ported from MainWindow.axaml.cs. The stage/photo sizes and the six physical
// callouts were measured against the shipping QuadStick.png, so the React view
// points at the same physical places as the Avalonia view rather than inventing
// a second diagram.
const STAGE = { width: 600, height: 468, photoX: 80, photoY: 84, photoW: 440, photoH: 293 } as const;
const ZONES: readonly ZoneDefinition[] = [
  { id: "joystick", titleKey: "Main_Joystick", shortKey: "Main_Joystick", labelX: 150, labelY: 390, pointX: 217, pointY: 253 },
  { id: "mp_left", titleKey: "Main_LeftMouthpieceHole", shortKey: "Main_Left", labelX: 38, labelY: 0, pointX: 218, pointY: 224 },
  { id: "mp_center", titleKey: "Main_CenterMouthpieceHole", shortKey: "Main_Center", labelX: 174, labelY: 0, pointX: 273, pointY: 224 },
  { id: "mp_right", titleKey: "Main_RightMouthpieceHole", shortKey: "Main_Right", labelX: 310, labelY: 0, pointX: 327, pointY: 224 },
  { id: "side", titleKey: "Main_SideTube", shortKey: "Main_SideTube", labelX: 446, labelY: 0, pointX: 407, pointY: 222 },
  { id: "lip", titleKey: "Main_LipSwitch", shortKey: "Main_LipSwitch", labelX: 318, labelY: 390, pointX: 269, pointY: 286 },
  { id: "combo", titleKey: "Main_HoleCombos", shortKey: "Main_Combos" },
  { id: "jacks", titleKey: "Main_SwitchJacks", shortKey: "Main_SwitchJacks" },
  { id: "other", titleKey: "Main_USBDevices", shortKey: "Main_USBDevices" },
  { id: "settings", titleKey: "Main_ModeSettings", shortKey: "Main_ModeSettings" },
  { id: "unset", titleKey: "Main_NoInputYet", shortKey: "Main_NoInputYet" },
] as const;

function zoneOf(input: string): ZoneId {
  if (input === "") return "unset";
  if (
    input.startsWith("mp_left_center") ||
    input.startsWith("mp_right_center") ||
    input.startsWith("mp_left_right") ||
    input.startsWith("mp_triple") ||
    input.startsWith("mp_right_mode")
  ) return "combo";
  if (input.startsWith("mp_left")) return "mp_left";
  if (input.startsWith("mp_center")) return "mp_center";
  if (input.startsWith("mp_right")) return "mp_right";
  if (["right_sip", "right_puff", "right_sip_soft", "right_puff_soft"].includes(input)) return "side";
  if (input === "lip") return "lip";
  if (input.startsWith("digital_in")) return "jacks";
  if (["left", "right", "up", "down", "any_direction", "center", "N", "NE", "E", "SE", "S", "SW", "W", "NW"].includes(input) || input.endsWith("_inner")) return "joystick";
  return "other";
}

function zonesForRow(row: VisualizerBinding): readonly ZoneId[] {
  const inputs = row.cells.slice(2, 10).map((value) => value.trim()).filter(Boolean);
  if (inputs.length === 0) return ["unset"];
  return [...new Set(inputs.map(zoneOf))];
}

function assignment(row: VisualizerBinding): string {
  return row.cells[11]?.trim() || row.cells[0]?.trim() || "—";
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
  if (frame.status.kind === "stale") return t("DevicePage_NothingIsReadingTheStick");
  if (frame.status.kind === "xinputOnly") return t("Main_ThisEmulationModeIsNot");
  return t("DevicePage_NothingIsReadingTheStick");
}

export function QuadStickVisualizer({
  client,
  rows,
  selectedRow,
  modeName,
  modeNumber,
  onSelectRow,
}: QuadStickVisualizerProps) {
  const { t } = useI18n();
  const [practice, setPractice] = useState(false);
  const [live, setLive] = useState<LiveSnapshot | null>(null);
  const [focusedZone, setFocusedZone] = useState(0);
  const hotspotRefs = useRef<Array<HTMLButtonElement | null>>([]);

  const rowsByZone = useMemo(() => {
    const map = new Map<ZoneId, VisualizerBinding[]>();
    for (const zone of ZONES) map.set(zone.id, []);
    for (const row of rows) {
      for (const zone of zonesForRow(row)) map.get(zone)?.push(row);
    }
    return map;
  }, [rows]);

  const selectedZones = useMemo(() => {
    const row = selectedRow === null ? undefined : rows.find((candidate) => candidate.row === selectedRow);
    return new Set(row === undefined ? [] : zonesForRow(row));
  }, [rows, selectedRow]);

  useEffect(() => {
    if (!practice) {
      setLive(null);
      return;
    }
    let disposed = false;
    let subscription: { dispose(): void } | null = null;
    void client.startLiveInput((frame) => {
      if (!disposed) setLive(frame);
    }).then((value) => {
      if (disposed) value.dispose();
      else subscription = value;
    }).catch(() => {
      if (!disposed) setLive(null);
    });
    return () => {
      disposed = true;
      subscription?.dispose();
      setLive(null);
    };
  }, [client, practice]);

  const photoZones = ZONES.filter((zone) => zone.labelX !== undefined);
  const extraZones = ZONES.filter((zone) => zone.labelX === undefined && (rowsByZone.get(zone.id)?.length ?? 0) > 0);

  const selectZone = (zone: ZoneDefinition): void => {
    const first = rowsByZone.get(zone.id)?.[0];
    if (first !== undefined) onSelectRow(first.row);
  };

  const onHotspotKeyDown = (event: KeyboardEvent<HTMLButtonElement>, index: number): void => {
    let next = index;
    if (event.key === "ArrowRight" || event.key === "ArrowDown") next = (index + 1) % photoZones.length;
    else if (event.key === "ArrowLeft" || event.key === "ArrowUp") next = (index - 1 + photoZones.length) % photoZones.length;
    else return;
    event.preventDefault();
    setFocusedZone(next);
    hotspotRefs.current[next]?.focus();
  };

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
          onClick={() => setPractice((value) => !value)}
        >
          {practice ? t("Main_UsingDeviceView") : t("DevicePage_JoystickTravel")}
        </button>
      </header>

      <div className="visualizer-stage-scroll">
        <div
          className="visualizer-stage"
          style={{ width: STAGE.width, height: STAGE.height }}
          dir="ltr"
        >
          <img
            className="quadstick-photo"
            src={quadStickPhoto}
            alt=""
            aria-hidden="true"
            style={{ left: STAGE.photoX, top: STAGE.photoY, width: STAGE.photoW, height: STAGE.photoH }}
          />
          {practice && live?.status.kind === "reading" ? (
            <span
              className="live-stick-dot"
              aria-hidden="true"
              style={{
                left: STAGE.photoX + 217 + live.status.motion.x * 30,
                top: STAGE.photoY + 169 + live.status.motion.y * 30,
              }}
            />
          ) : null}
          {photoZones.map((zone, index) => {
            const count = rowsByZone.get(zone.id)?.length ?? 0;
            const selected = selectedZones.has(zone.id);
            const active = zone.id === "joystick" && practice && liveJoystickActive(live);
            return (
              <div className="hotspot-group" key={zone.id}>
                <span
                  className="hotspot-marker"
                  aria-hidden="true"
                  data-active={active ? "true" : undefined}
                  style={{ left: zone.pointX, top: zone.pointY }}
                />
                <svg className="hotspot-line" aria-hidden="true" viewBox={`0 0 ${String(STAGE.width)} ${String(STAGE.height)}`}>
                  <line
                    x1={(zone.labelX ?? 0) + 58}
                    y1={(zone.labelY ?? 0) < (zone.pointY ?? 0) ? (zone.labelY ?? 0) + 68 : (zone.labelY ?? 0)}
                    x2={zone.pointX}
                    y2={zone.pointY}
                  />
                </svg>
                <button
                  ref={(element) => { hotspotRefs.current[index] = element; }}
                  type="button"
                  className="visualizer-hotspot"
                  aria-pressed={selected}
                  data-live-active={active ? "true" : undefined}
                  tabIndex={focusedZone === index ? 0 : -1}
                  style={{ left: zone.labelX, top: zone.labelY }}
                  onFocus={() => setFocusedZone(index)}
                  onKeyDown={(event) => onHotspotKeyDown(event, index)}
                  onClick={() => selectZone(zone)}
                >
                  <strong>{t(zone.shortKey)}</strong>
                  <span>{count === 0 ? t("Main_NotMapped") : t("Count_Mapping_other", [count])}</span>
                  {active ? <span className="live-word">{t("Main_UsingDeviceView")}</span> : null}
                </button>
              </div>
            );
          })}
        </div>
      </div>

      {extraZones.length > 0 ? (
        <div className="visualizer-extra-zones" aria-label={t("Main_Parts")}>
          {extraZones.map((zone) => (
            <button key={zone.id} type="button" onClick={() => selectZone(zone)}>
              <strong>{t(zone.titleKey)}</strong>
              <span>{t("Count_Mapping_other", [rowsByZone.get(zone.id)?.length ?? 0])}</span>
            </button>
          ))}
        </div>
      ) : null}

      <p className="practice-status" role="status" aria-live="polite">
        {practice ? liveText(live, t) : t("Main_WhatALitRowMeans")}
      </p>

      <div className="semantic-parts" aria-label={t("Main_Parts")}>
        {ZONES.map((zone) => {
          const zoneRows = rowsByZone.get(zone.id) ?? [];
          if (zoneRows.length === 0) return null;
          return (
            <section key={zone.id} aria-labelledby={`semantic-zone-${zone.id}`}>
              <h3 id={`semantic-zone-${zone.id}`}>{t(zone.titleKey)}</h3>
              <ul>
                {zoneRows.map((row) => (
                  <li key={`${zone.id}-${String(row.row)}`}>
                    <button
                      type="button"
                      aria-pressed={selectedRow === row.row}
                      onClick={() => onSelectRow(row.row)}
                    >
                      <span>{assignment(row)}</span>
                      <span>{row.cells.slice(2, 10).filter((value) => value.trim() !== "").join(" · ")}</span>
                    </button>
                  </li>
                ))}
              </ul>
            </section>
          );
        })}
      </div>
    </section>
  );
}
