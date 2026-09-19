/** Avalonia DeviceDiagram — FPS / Original / Singleton. */

export type QsModel = "fps" | "original" | "singleton";

export interface Hotspot {
  readonly zone: string;
  readonly labelX: number;
  readonly bottom: boolean;
  readonly pointX: number;
  readonly pointY: number;
}

export interface ModeLightRow {
  readonly x: number;
  readonly gap: number;
  readonly y: number;
  readonly points?: readonly number[];
}

export interface DeviceDiagramSpec {
  readonly model: QsModel;
  readonly nativeW: number;
  readonly nativeH: number;
  readonly source: { x: number; y: number; w: number; h: number };
  readonly photoX: number;
  readonly photoY: number;
  readonly photoW: number;
  readonly hotspots: readonly Hotspot[];
  readonly lights: ModeLightRow | null;
  readonly zones: readonly string[];
}

export const STAGE_W = 910;
export const PILL_W = 220;
export const PILL_H = 116;
export const SMALL_PILL_H = 150;
export const TOP_CALLOUT_ROOM = 116;
export const TOP_CALLOUT_GAP = 10;

const FULL_ZONES = [
  "joystick",
  "mp_left",
  "mp_center",
  "mp_right",
  "combo",
  "side",
  "lip",
  "jacks",
  "other",
] as const;

const SINGLETON_ZONES = ["joystick", "mp_center", "other"] as const;

export const FPS_DIAGRAM: DeviceDiagramSpec = {
  model: "fps",
  nativeW: 1536,
  nativeH: 1024,
  source: { x: 0, y: 0, w: 1, h: 1 },
  photoX: 175,
  photoY: 132,
  photoW: 560,
  hotspots: [
    { zone: "mp_left", labelX: 0, bottom: false, pointX: 0.3841, pointY: 0.544 },
    { zone: "mp_center", labelX: 230, bottom: false, pointX: 0.4824, pointY: 0.544 },
    { zone: "mp_right", labelX: 460, bottom: false, pointX: 0.5801, pointY: 0.544 },
    { zone: "side", labelX: 690, bottom: false, pointX: 0.7266, pointY: 0.545 },
    { zone: "joystick", labelX: 245, bottom: true, pointX: 0.36, pointY: 0.57 },
    { zone: "lip", labelX: 475, bottom: true, pointX: 0.4883, pointY: 0.677 },
  ],
  lights: { x: 0.3275, gap: 0.0862, y: 0.1064 },
  zones: FULL_ZONES,
};

export const ORIGINAL_DIAGRAM: DeviceDiagramSpec = {
  ...FPS_DIAGRAM,
  model: "original",
  hotspots: [
    { zone: "mp_left", labelX: 0, bottom: false, pointX: 0.3136, pointY: 0.4778 },
    { zone: "mp_center", labelX: 230, bottom: false, pointX: 0.4386, pointY: 0.4778 },
    { zone: "mp_right", labelX: 460, bottom: false, pointX: 0.5614, pointY: 0.4778 },
    { zone: "side", labelX: 690, bottom: false, pointX: 0.7432, pointY: 0.471 },
    { zone: "joystick", labelX: 245, bottom: true, pointX: 0.3114, pointY: 0.5768 },
    { zone: "lip", labelX: 475, bottom: true, pointX: 0.4295, pointY: 0.6894 },
  ],
  lights: { x: 0.2407, gap: 0.1, y: 0.1729 },
};

export const SINGLETON_DIAGRAM: DeviceDiagramSpec = {
  model: "singleton",
  nativeW: 2048,
  nativeH: 2048,
  source: { x: 0.2124, y: 0.1738, w: 0.5747, h: 0.6523 },
  photoX: 290.54,
  photoY: 132,
  photoW: 328.92,
  hotspots: [
    { zone: "joystick", labelX: 70, bottom: false, pointX: 0.375, pointY: 0.5 },
    { zone: "mp_center", labelX: 690, bottom: false, pointX: 0.48, pointY: 0.715 },
  ],
  lights: {
    x: 690 / 2048,
    gap: 0,
    y: 498 / 2048,
    points: [690 / 2048, 815 / 2048, 935 / 2048, 1048 / 2048, 1159 / 2048],
  },
  zones: SINGLETON_ZONES,
};

export function diagramFor(model: QsModel): DeviceDiagramSpec {
  switch (model) {
    case "original":
      return ORIGINAL_DIAGRAM;
    case "singleton":
      return SINGLETON_DIAGRAM;
    default:
      return FPS_DIAGRAM;
  }
}

export function photoHeight(diagram: DeviceDiagramSpec): number {
  const { photoW, source, nativeW, nativeH } = diagram;
  return (photoW / source.w) * source.h * (nativeH / nativeW);
}

export function onPhoto(
  diagram: DeviceDiagramSpec,
  fx: number,
  fy: number,
): { x: number; y: number } {
  const { source, photoW } = diagram;
  const photoH = photoHeight(diagram);
  return {
    x: ((fx - source.x) / source.w) * photoW,
    y: ((fy - source.y) / source.h) * photoH,
  };
}

export function lightX(row: ModeLightRow, index: number): number {
  if (row.points !== undefined && row.points[index] !== undefined) return row.points[index]!;
  return row.x + index * row.gap;
}

export function devicePhotoY(diagram: DeviceDiagramSpec): number {
  return diagram.photoY + TOP_CALLOUT_ROOM;
}

export function deviceBottomLabelY(diagram: DeviceDiagramSpec): number {
  return devicePhotoY(diagram) + photoHeight(diagram) + 13;
}

export function deviceStageHeight(diagram: DeviceDiagramSpec): number {
  const hasBottom = diagram.hotspots.some((spot) => spot.bottom);
  if (hasBottom) return deviceBottomLabelY(diagram) + SMALL_PILL_H + 20;
  return devicePhotoY(diagram) + photoHeight(diagram) + 20;
}

/** Avalonia ComboPlaces — hole pairings on the diagram. */
export const COMBO_PLACES: readonly {
  input: string;
  parts: readonly string[];
  labelX: number;
  bottom: boolean;
  rise: number;
}[] = [
  { input: "mp_left_center_sip", parts: ["mp_left", "mp_center"], labelX: 0, bottom: false, rise: 0 },
  { input: "mp_left_right_sip", parts: ["mp_left", "mp_right"], labelX: 230, bottom: false, rise: -0.06 },
  { input: "mp_right_center_sip", parts: ["mp_center", "mp_right"], labelX: 460, bottom: false, rise: 0 },
  { input: "mp_right_mode_sip", parts: ["mp_right", "side"], labelX: 690, bottom: false, rise: 0 },
  { input: "mp_triple_sip", parts: ["mp_left", "mp_center", "mp_right"], labelX: 245, bottom: true, rise: 0.055 },
];

/** Photo-fraction points for one pairing's holes, or empty when the model lacks a part. */
export function comboPoints(
  diagram: DeviceDiagramSpec,
  input: string,
): readonly { x: number; y: number }[] {
  const place = COMBO_PLACES.find((entry) => entry.input === input);
  if (place === undefined) return [];
  const byZone = new Map(diagram.hotspots.map((spot) => [spot.zone, spot]));
  const points: { x: number; y: number }[] = [];
  for (const part of place.parts) {
    const spot = byZone.get(part);
    if (spot === undefined) return [];
    points.push({ x: spot.pointX, y: spot.pointY });
  }
  return points;
}

export function comboHotspots(diagram: DeviceDiagramSpec): Hotspot[] {
  const result: Hotspot[] = [];
  for (const place of COMBO_PLACES) {
    const points = comboPoints(diagram, place.input);
    if (points.length === 0) continue;
    result.push({
      zone: place.input,
      labelX: place.labelX,
      bottom: place.bottom,
      pointX: points.reduce((sum, point) => sum + point.x, 0) / points.length,
      pointY: points.reduce((sum, point) => sum + point.y, 0) / points.length + place.rise,
    });
  }
  return result;
}
