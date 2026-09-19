/** Avalonia DeviceDiagram FPS geometry (stage + photo + hotspots). */

export interface Hotspot {
  readonly zone: string;
  readonly labelX: number;
  readonly bottom: boolean;
  readonly pointX: number;
  readonly pointY: number;
}

export interface DeviceDiagramSpec {
  readonly model: "fps" | "original" | "singleton";
  readonly asset: string;
  readonly nativeW: number;
  readonly nativeH: number;
  readonly source: { x: number; y: number; w: number; h: number };
  readonly photoX: number;
  readonly photoY: number;
  readonly photoW: number;
  readonly hotspots: readonly Hotspot[];
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

export const FPS_DIAGRAM: DeviceDiagramSpec = {
  model: "fps",
  asset: "QuadStickFPS.png",
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
  zones: FULL_ZONES,
};

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
