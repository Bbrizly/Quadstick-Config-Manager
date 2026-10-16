import { useState } from "react";

import { useI18n, type MessageKey } from "../../i18n";
import type { LiveSnapshot, ModelChoice, QcmClient } from "../../platform";
import { useLiveInput } from "../live/useLiveInput";
import { diagramFor, lightX, onPhoto, photoHeight } from "../visualizer/deviceDiagram";
import { photoFor } from "../visualizer/photos";

/** `DeviceBand.CategoryParts`: the photo parts each settings group is about. */
const CATEGORY_PARTS: Readonly<Record<string, readonly string[]>> = {
  Joystick: ["joystick"],
  Mouse: ["joystick"],
  "Sip and puff": ["mp_left", "mp_center", "mp_right", "side"],
  "Lip sensor": ["lip"],
  "Sound and lights": ["leds"],
};

const PART_NAME: Readonly<Record<string, MessageKey>> = {
  joystick: "Main_Joystick",
  mp_left: "Main_LeftMouthpieceHole",
  mp_center: "Main_CenterMouthpieceHole",
  mp_right: "Main_RightMouthpieceHole",
  side: "Main_SideTube",
  lip: "Main_LipSwitch",
  leds: "DevicePage_TheModeLightsOnThe",
};

const PAD = 128;
const PHOTO_HEIGHT = 126;

export interface DeviceBandProps {
  readonly client: QcmClient;
  readonly category: string;
  readonly model: ModelChoice;
  /** What prefs.csv says a number is, else the catalog default. */
  readonly numberOf: (name: string, fallback: number) => number;
}

/**
 * The picture above the device settings (`DeviceBand.cs`): the photo with a
 * ring on the parts the open group changes, and for the joystick's two
 * groups a pad drawing its dead zone and full-signal circles. Every cue is
 * said again in the words beside it. The stick is read only while a joystick
 * group is open, so the other groups subscribe to nothing.
 */
export function DeviceBand(props: DeviceBandProps) {
  const joystick = (CATEGORY_PARTS[props.category] ?? []).includes("joystick");
  return joystick ? <LiveBand {...props} /> : <Band {...props} live={null} />;
}

function LiveBand(props: DeviceBandProps) {
  const [live, setLive] = useState<LiveSnapshot | null>(null);
  useLiveInput(props.client, setLive);
  return <Band {...props} live={live} />;
}

function Band({ category, model, numberOf, live }: DeviceBandProps & { readonly live: LiveSnapshot | null }) {
  const { t } = useI18n();
  const diagram = diagramFor(model);
  // A part this model has not got gets no ring: no lip switch on a Singleton.
  const parts = (CATEGORY_PARTS[category] ?? []).filter(
    (part) => part === "leds" || diagram.zones.includes(part),
  );
  const joystick = parts.includes("joystick");
  const photoW = diagram.photoW;
  const photoH = photoHeight(diagram);
  const fullW = photoW / diagram.source.w;
  const fullH = photoH / diagram.source.h;
  const reading = live?.status.kind === "reading" ? live.status : null;

  const rings = parts.flatMap((part) => {
    const spot = diagram.hotspots.find((hotspot) => hotspot.zone === part);
    if (spot === undefined) return [];
    const at = onPhoto(diagram, spot.pointX, spot.pointY);
    return [
      <circle key={`${part}-under`} cx={at.x} cy={at.y} r={23} className="band-ring-under" />,
      <circle key={`${part}-over`} cx={at.x} cy={at.y} r={23} className="band-ring-over" />,
    ];
  });

  // The case lights at the brightness the file asks for.
  const lights =
    parts.includes("leds") && diagram.lights !== null
      ? Array.from({ length: 5 }, (_, index) => {
          const row = diagram.lights!;
          const at = onPhoto(diagram, lightX(row, index), row.y);
          const bright = Math.min(1, Math.max(0.08, numberOf("brightness", 75) / 100));
          return (
            <g key={`led-${String(index)}`} opacity={bright}>
              <circle cx={at.x} cy={at.y} r={14} className="band-led-halo" />
              <circle cx={at.x} cy={at.y} r={7} className="band-led" />
            </g>
          );
        })
      : [];

  // `DrawLiveStick`: where the stick is, on the picture of the stick.
  let stick = null;
  const stickSpot = diagram.hotspots.find((hotspot) => hotspot.zone === "joystick");
  if (reading !== null && stickSpot !== undefined) {
    const at = onPhoto(diagram, stickSpot.pointX, stickSpot.pointY);
    const reach = 20;
    const x = at.x + reading.motion.x * reach;
    const y = at.y + reading.motion.y * reach;
    stick = (
      <g>
        <circle cx={at.x} cy={at.y} r={reach} className="band-stick-home" />
        <circle cx={x} cy={y} r={10} className="band-stick-under" />
        <circle cx={x} cy={y} r={6.5} className="band-stick-dot" />
      </g>
    );
  }

  const dead = Math.min(100, Math.max(0, numberOf("joystick_deflection_minimum", 8))) / 100;
  const full = Math.min(100, Math.max(1, numberOf("joystick_deflection_maximum", 25))) / 100;
  const half = PAD / 2;

  const partsText =
    parts.length === 0
      ? t("DevicePage_ThisGroupChangesNothingYou")
      : t("DevicePage_ThisGroupChangesParts", [parts.map((part) => t(PART_NAME[part] ?? "Main_Joystick")).join(", ")]);

  return (
    <div className="device-band">
      <svg
        className="device-band-photo"
        viewBox={`0 0 ${String(photoW)} ${String(photoH)}`}
        height={PHOTO_HEIGHT}
        width={(PHOTO_HEIGHT * photoW) / photoH}
        aria-hidden="true"
        focusable="false"
      >
        <svg x={0} y={0} width={photoW} height={photoH} overflow="hidden">
          <image
            href={photoFor(model)}
            x={-diagram.source.x * fullW}
            y={-diagram.source.y * fullH}
            width={fullW}
            height={fullH}
            preserveAspectRatio="none"
          />
        </svg>
        {lights}
        {rings}
        {stick}
      </svg>
      {joystick ? (
        <div className="device-band-pad">
          <svg
            width={PAD}
            height={PAD}
            viewBox={`0 0 ${String(PAD)} ${String(PAD)}`}
            role="img"
            aria-label={t("DevicePage_HowFarTheStickHas")}
          >
            <rect x={0.5} y={0.5} width={PAD - 1} height={PAD - 1} rx={10} className="band-pad-plate" />
            {dead * half >= 1 ? <circle cx={half} cy={half} r={dead * half} className="band-pad-dead" /> : null}
            {full * half >= 1 ? <circle cx={half} cy={half} r={full * half} className="band-pad-full" /> : null}
          </svg>
          <span className="secondary">{t("DevicePage_JoystickTravel")}</span>
        </div>
      ) : null}
      <div className="device-band-words">
        <strong>{partsText}</strong>
        {joystick ? (
          <>
            <span className="secondary">
              {t("DevicePage_DashedRingDeadZone", [Math.round(dead * 100), Math.round(full * 100)])}
            </span>
            {/* These move while nobody is looking at them; found on demand,
                not read out (`AutomationLiveSetting.Off`). */}
            <span className="secondary">
              {reading === null
                ? t("DevicePage_NothingIsReadingTheStick")
                : t("DevicePage_TheStickIsAtXY", [Math.round(reading.motion.x * 100), Math.round(-reading.motion.y * 100)])}
            </span>
          </>
        ) : null}
        {reading === null ? null : (
          <span className="secondary">
            {reading.motion.buttons.length === 0
              ? t("DevicePage_ReadingProductNothingPressed", [reading.product])
              : t("DevicePage_ReadingProductPressedNow", [reading.product, reading.motion.buttons.join(", ")])}
          </span>
        )}
      </div>
    </div>
  );
}
