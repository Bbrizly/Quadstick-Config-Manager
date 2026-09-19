/** Avalonia ModeLights — FW 2373 update_active_config_leds. */

export type ModeLight = "off" | "red" | "blue" | "purple";

const RANGE_PATTERNS = [0, 0b0000100001, 0b0000100000, 0b0000000001, 0b1111100000, 0b1111100001, 0b0000011111];

export const HIGHEST_MODE = 34;

/** Five lights left to right, or null when the firmware has no pattern. */
export function modeLightsFor(mode: number): ModeLight[] | null {
  if (mode < 1 || mode > HIGHEST_MODE) return null;
  const bits =
    ((mode === 15 ? 0 : 0b10000100000 >> (mode % 5)) | RANGE_PATTERNS[Math.floor(mode / 5)]!) &
    0b1111111111;
  const lights: ModeLight[] = [];
  for (let bit = 0; bit < 5; bit += 1) {
    const red = (bits & (1 << bit)) !== 0;
    const blue = (bits & (1 << (bit + 5))) !== 0;
    let colour: ModeLight = "off";
    if (red && blue) colour = "purple";
    else if (red) colour = "red";
    else if (blue) colour = "blue";
    lights[4 - bit] = colour;
  }
  return lights;
}

export function ledCss(light: ModeLight): string {
  switch (light) {
    case "purple":
      return "#c25cff";
    case "blue":
      return "#4c8dff";
    case "red":
      return "#ff4d45";
    default:
      return "transparent";
  }
}
