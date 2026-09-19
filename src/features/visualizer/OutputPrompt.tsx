/**
 * Compact controller prompts (Avalonia OutputVisuals + Xelu pack).
 * Face/dpad/mouse from assets; sticks and shoulders drawn as vectors.
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

/** Mouse silhouettes need a word; stick directions are self-describing (wedge). */
const REQUIRES_TEXT_LABEL = new Set([
  "mouse_left",
  "mouse_right",
  "mouse_up",
  "mouse_down",
]);

type StickDir = "left" | "right" | "up" | "down";

const STICK_DIRS: Record<string, { side: "L" | "R"; dir: StickDir }> = {
  left_joy_left: { side: "L", dir: "left" },
  left_joy_right: { side: "L", dir: "right" },
  left_joy_up: { side: "L", dir: "up" },
  left_joy_down: { side: "L", dir: "down" },
  right_joy_left: { side: "R", dir: "left" },
  right_joy_right: { side: "R", dir: "right" },
  right_joy_up: { side: "R", dir: "up" },
  right_joy_down: { side: "R", dir: "down" },
};

const SHOULDERS: Record<string, { mark: string; trigger: boolean }> = {
  left_1: { mark: "L1", trigger: false },
  right_1: { mark: "R1", trigger: false },
  left_bumper: { mark: "LB", trigger: false },
  right_bumper: { mark: "RB", trigger: false },
  left_2: { mark: "L2", trigger: true },
  right_2: { mark: "R2", trigger: true },
  left_trigger: { mark: "LT", trigger: true },
  right_trigger: { mark: "RT", trigger: true },
};

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

/** True when OutputPrompt can draw art (PNG or vector). */
export function hasPromptArt(token: string): boolean {
  return promptSrc(token) !== null || token in STICK_DIRS || token in SHOULDERS;
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

function directionVector(dir: StickDir): { x: number; y: number } {
  switch (dir) {
    case "up":
      return { x: 0, y: -1 };
    case "down":
      return { x: 0, y: 1 };
    case "left":
      return { x: -1, y: 0 };
    case "right":
      return { x: 1, y: 0 };
  }
}

/** Avalonia OutputVisuals.Joystick — well + rim wedge + L/R letter. */
function StickDirectionPrompt({
  side,
  dir,
  size,
  label,
}: {
  readonly side: "L" | "R";
  readonly dir: StickDir;
  readonly size: number;
  readonly label: string;
}) {
  const { x: dx, y: dy } = directionVector(dir);
  const tip = { x: 128 + dx * 125, y: 128 + dy * 125 };
  const back = { x: 128 + dx * 97, y: 128 + dy * 97 };
  const px = -dy * 28;
  const py = dx * 28;
  const letterSize = size * 0.52;
  return (
    <span className="stick-prompt" title={label} aria-hidden="true">
      <span className="stick-side" style={{ fontSize: `${String(letterSize)}px` }}>
        {side}
      </span>
      <svg
        className="output-prompt stick-well"
        width={size}
        height={size}
        viewBox="0 0 256 256"
        role="img"
      >
        <circle cx="128" cy="128" r="95" className="stick-well-outer" />
        <circle cx="128" cy="128" r="75" className="stick-well-face" />
        <circle cx="128" cy="128" r="32" className="stick-socket" />
        <polygon
          className="stick-wedge"
          points={`${String(tip.x)},${String(tip.y)} ${String(back.x + px)},${String(back.y + py)} ${String(back.x - px)},${String(back.y - py)}`}
        />
        <circle cx="128" cy="128" r="39" className="stick-cap" />
        <circle cx="128" cy="128" r="26" className="stick-grip" />
      </svg>
    </span>
  );
}

/** Avalonia OutputVisuals.Shoulder — bumper bar vs trigger paddle. */
function ShoulderPrompt({
  mark,
  trigger,
  size,
  label,
}: {
  readonly mark: string;
  readonly trigger: boolean;
  readonly size: number;
  readonly label: string;
}) {
  const height = size;
  const width = trigger ? size : size * 1.4;
  const bodyHeight = trigger ? height : height * 0.6;
  return (
    <span
      className={trigger ? "shoulder-prompt trigger" : "shoulder-prompt bumper"}
      title={label}
      aria-hidden="true"
      style={{
        width: `${String(width)}px`,
        height: `${String(height)}px`,
      }}
    >
      <span
        className="shoulder-body"
        style={{
          width: `${String(width)}px`,
          height: `${String(bodyHeight)}px`,
          borderRadius: trigger
            ? `${String(width * 0.46)}px ${String(width * 0.46)}px ${String(width * 0.16)}px ${String(width * 0.16)}px`
            : `${String(bodyHeight / 2)}px`,
          marginTop: trigger ? `${String(height * 0.2)}px` : undefined,
          fontSize: size <= 30 ? "11px" : "14px",
        }}
      >
        {mark}
      </span>
    </span>
  );
}

export function OutputPrompt({
  token,
  label,
  size = 30,
}: {
  readonly token: string;
  readonly label: string;
  readonly size?: number;
}) {
  const stick = STICK_DIRS[token];
  if (stick !== undefined) {
    return <StickDirectionPrompt side={stick.side} dir={stick.dir} size={size} label={label} />;
  }
  const shoulder = SHOULDERS[token];
  if (shoulder !== undefined) {
    return (
      <ShoulderPrompt
        mark={shoulder.mark}
        trigger={shoulder.trigger}
        size={size}
        label={label}
      />
    );
  }

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

/** Stick-click prompts still use Xelu assets (left_stick / right_stick). */
export function stickClickSrc(side: "left" | "right"): string {
  return side === "left" ? xboxLeftStick : xboxRightStick;
}
