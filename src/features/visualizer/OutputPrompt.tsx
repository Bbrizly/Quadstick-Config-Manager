/**
 * Compact controller prompts (Avalonia OutputVisuals + Xelu pack).
 * Face, dpad, sticks, shoulders, mouse — presentation only.
 */

import playstationCircle from "../../QuadStick.App/Assets/OutputVisuals/Playstation/Playstation0021.png";
import playstationTriangle from "../../QuadStick.App/Assets/OutputVisuals/Playstation/Playstation0022.png";
import playstationSquare from "../../QuadStick.App/Assets/OutputVisuals/Playstation/Playstation0023.png";
import playstationCross from "../../QuadStick.App/Assets/OutputVisuals/Playstation/Playstation0024.png";
import playstationLeftStick from "../../QuadStick.App/Assets/OutputVisuals/Playstation/Playstation0027.png";
import playstationRightStick from "../../QuadStick.App/Assets/OutputVisuals/Playstation/Playstation0028.png";
import playstationDpad from "../../QuadStick.App/Assets/OutputVisuals/Playstation/Playstation0029.png";
import playstationDpadN from "../../QuadStick.App/Assets/OutputVisuals/Playstation/Playstation0030.png";
import playstationDpadE from "../../QuadStick.App/Assets/OutputVisuals/Playstation/Playstation0031.png";
import playstationDpadS from "../../QuadStick.App/Assets/OutputVisuals/Playstation/Playstation0032.png";
import playstationDpadW from "../../QuadStick.App/Assets/OutputVisuals/Playstation/Playstation0033.png";
import xboxA from "../../QuadStick.App/Assets/OutputVisuals/Xbox/Xbox0001.png";
import xboxB from "../../QuadStick.App/Assets/OutputVisuals/Xbox/Xbox0002.png";
import xboxY from "../../QuadStick.App/Assets/OutputVisuals/Xbox/Xbox0003.png";
import xboxX from "../../QuadStick.App/Assets/OutputVisuals/Xbox/Xbox0004.png";
import xboxLb from "../../QuadStick.App/Assets/OutputVisuals/Xbox/Xbox0005.png";
import xboxRb from "../../QuadStick.App/Assets/OutputVisuals/Xbox/Xbox0006.png";
import xboxLeftStick from "../../QuadStick.App/Assets/OutputVisuals/Xbox/Xbox0007.png";
import xboxRightStick from "../../QuadStick.App/Assets/OutputVisuals/Xbox/Xbox0008.png";
import xboxDpad from "../../QuadStick.App/Assets/OutputVisuals/Xbox/Xbox0009.png";
import xboxDpadN from "../../QuadStick.App/Assets/OutputVisuals/Xbox/Xbox0010.png";
import xboxDpadE from "../../QuadStick.App/Assets/OutputVisuals/Xbox/Xbox0011.png";
import mouseBody from "../../QuadStick.App/Assets/OutputVisuals/KeyboardLight/KeyLight0068.png";
import mouseLeft from "../../QuadStick.App/Assets/OutputVisuals/KeyboardLight/KeyLight0069.png";
import mouseRight from "../../QuadStick.App/Assets/OutputVisuals/KeyboardLight/KeyLight0070.png";
import mouseMiddle from "../../QuadStick.App/Assets/OutputVisuals/KeyboardLight/KeyLight0071.png";
import mouseWheelUp from "../../QuadStick.App/Assets/OutputVisuals/KeyboardLight/KeyLight0072.png";
import mouseWheelDown from "../../QuadStick.App/Assets/OutputVisuals/KeyboardLight/KeyLight0073.png";

const PROMPTS: Record<string, string> = {
  cross: playstationCross,
  x: playstationCross,
  circle: playstationCircle,
  square: playstationSquare,
  triangle: playstationTriangle,
  left_stick: playstationLeftStick,
  right_stick: playstationRightStick,
  left_3: playstationLeftStick,
  right_3: playstationRightStick,
  dpad: playstationDpad,
  dpad_xbox: xboxDpad,
  dpad_N: playstationDpadN,
  dpad_E: playstationDpadE,
  dpad_S: playstationDpadS,
  dpad_W: playstationDpadW,
  A: xboxA,
  B: xboxB,
  Y: xboxY,
  X: xboxX,
  left_1: xboxLb,
  right_1: xboxRb,
  left_bumper: xboxLb,
  right_bumper: xboxRb,
  left_2: xboxLb,
  right_2: xboxRb,
  left_trigger: xboxLb,
  right_trigger: xboxRb,
  left_joy_left: xboxLeftStick,
  left_joy_right: xboxLeftStick,
  left_joy_up: xboxLeftStick,
  left_joy_down: xboxLeftStick,
  right_joy_left: xboxRightStick,
  right_joy_right: xboxRightStick,
  right_joy_up: xboxRightStick,
  right_joy_down: xboxRightStick,
  mouse_left_button: mouseLeft,
  mouse_right_button: mouseRight,
  mouse_middle_button: mouseMiddle,
  mouse_wheel_up: mouseWheelUp,
  mouse_wheel_down: mouseWheelDown,
  mouse_left: mouseBody,
  mouse_right: mouseBody,
  mouse_up: mouseBody,
  mouse_down: mouseBody,
  dpad_n: xboxDpadN,
  dpad_e: xboxDpadE,
  dpad_s: xboxDpadN,
  dpad_w: xboxDpadE,
};

/** Stick / mouse directions need a word beside the body art (Avalonia RequiresTextLabel). */
const REQUIRES_TEXT_LABEL = new Set([
  "left_joy_left",
  "left_joy_right",
  "left_joy_up",
  "left_joy_down",
  "right_joy_left",
  "right_joy_right",
  "right_joy_up",
  "right_joy_down",
  "mouse_left",
  "mouse_right",
  "mouse_up",
  "mouse_down",
]);

/**
 * HID button index (1-based, mode-0 PS3 report) → profile output words.
 * Matches Avalonia LiveInput.Ps3Buttons / FW 2373 output_keywords.h.
 */
export const BUTTON_OUTPUTS: Record<number, readonly string[]> = {
  1: ["square", "X", "xac_left_up", "xac_right_view"],
  2: ["x", "A", "xac_left_down", "xac_right_menu"],
  3: ["circle", "B", "xac_left_LS", "xac_right_RS"],
  4: ["triangle", "Y", "xac_left_LB", "xac_right_RB"],
  5: ["left_1", "left_bumper", "xac_left_A", "xac_right_X"],
  6: ["right_1", "right_bumper", "xac_left_B", "xac_right_Y"],
  7: ["left_2", "left_trigger", "xac_left_view", "xac_right_up"],
  8: ["right_2", "right_trigger", "xac_left_menu", "xac_right_down"],
  9: ["select", "back"],
  10: ["start"],
  11: ["left_3", "left_stick"],
  12: ["right_3", "right_stick"],
  13: ["ps3", "guide"],
};

export function promptSrc(token: string): string | null {
  return PROMPTS[token] ?? null;
}

export function requiresTextLabel(token: string): boolean {
  return REQUIRES_TEXT_LABEL.has(token);
}

export function outputsForButtons(buttons: readonly number[]): ReadonlySet<string> {
  const out = new Set<string>();
  for (const button of buttons) {
    const tokens = BUTTON_OUTPUTS[button];
    if (tokens === undefined) continue;
    for (const token of tokens) out.add(token);
  }
  return out;
}

export function OutputPrompt({
  token,
  label,
  size = 22,
}: {
  readonly token: string;
  readonly label: string;
  readonly size?: number;
}) {
  const src = promptSrc(token);
  if (src === null) {
    return <span className="output-prompt-text">{label}</span>;
  }
  const rotate =
    token === "dpad_S" || token === "dpad_s"
      ? "180deg"
      : token === "dpad_W" || token === "dpad_w"
        ? "270deg"
        : token === "dpad_E" || token === "dpad_e"
          ? "90deg"
          : undefined;
  return (
    <img
      className="output-prompt"
      src={src}
      alt=""
      width={size}
      height={size}
      title={label}
      aria-hidden="true"
      draggable={false}
      style={rotate === undefined ? undefined : { transform: `rotate(${rotate})` }}
    />
  );
}
