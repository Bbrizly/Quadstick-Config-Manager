/** Compact controller prompts for callouts (Avalonia OutputVisuals face/dpad). */

import playstationCircle from "../../QuadStick.App/Assets/OutputVisuals/Playstation/Playstation0021.png";
import playstationTriangle from "../../QuadStick.App/Assets/OutputVisuals/Playstation/Playstation0022.png";
import playstationSquare from "../../QuadStick.App/Assets/OutputVisuals/Playstation/Playstation0023.png";
import playstationCross from "../../QuadStick.App/Assets/OutputVisuals/Playstation/Playstation0024.png";
import playstationDpad from "../../QuadStick.App/Assets/OutputVisuals/Playstation/Playstation0029.png";
import playstationDpadN from "../../QuadStick.App/Assets/OutputVisuals/Playstation/Playstation0030.png";
import playstationDpadE from "../../QuadStick.App/Assets/OutputVisuals/Playstation/Playstation0031.png";
import playstationDpadS from "../../QuadStick.App/Assets/OutputVisuals/Playstation/Playstation0032.png";
import playstationDpadW from "../../QuadStick.App/Assets/OutputVisuals/Playstation/Playstation0033.png";
import xboxA from "../../QuadStick.App/Assets/OutputVisuals/Xbox/Xbox0001.png";
import xboxB from "../../QuadStick.App/Assets/OutputVisuals/Xbox/Xbox0002.png";
import xboxY from "../../QuadStick.App/Assets/OutputVisuals/Xbox/Xbox0003.png";
import xboxX from "../../QuadStick.App/Assets/OutputVisuals/Xbox/Xbox0004.png";

const PROMPTS: Record<string, string> = {
  cross: playstationCross,
  circle: playstationCircle,
  square: playstationSquare,
  triangle: playstationTriangle,
  dpad: playstationDpad,
  dpad_N: playstationDpadN,
  dpad_E: playstationDpadE,
  dpad_S: playstationDpadS,
  dpad_W: playstationDpadW,
  A: xboxA,
  B: xboxB,
  Y: xboxY,
  X: xboxX,
};

export function promptSrc(token: string): string | null {
  return PROMPTS[token] ?? null;
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
    />
  );
}
