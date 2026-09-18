import { useState } from "react";

import { Dialog } from "../../components/primitives/Dialog";
import { useI18n, type MessageKey } from "../../i18n";

/** Soft tour: callout steps only. Spotlight targeting lands with AT hardening. */
const STEPS: readonly { title: MessageKey | string; body: MessageKey }[] = [
  { title: "Welcome", body: "Tour_ThisShowsYouHowTo" },
  { title: "Settings_Appearance", body: "Tour_SetLightOrDarkTo" },
  { title: "Tour_NewProfile", body: "Tour_EveryProfileStartsFromA" },
  { title: "Your QuadStick", body: "Tour_ThisIsYourQuadStickEach" },
  { title: "Tour_PickAPart", body: "Tour_PickAPartToSee" },
  { title: "Save", body: "Tour_SaveYourWorkToA" },
  { title: "Install", body: "Tour_WhenItSReadySend" },
  { title: "Done", body: "Tour_YouCanReplayThisAnytime" },
];

export interface TutorialTourProps {
  readonly open: boolean;
  readonly onDone: () => void;
}

export function TutorialTour({ open, onDone }: TutorialTourProps) {
  const { t } = useI18n();
  const [index, setIndex] = useState(0);
  if (!open) return null;

  const step = STEPS[index] ?? STEPS[0]!;
  const title =
    step.title.startsWith("Tour_") || step.title.startsWith("Settings_")
      ? t(step.title as MessageKey)
      : step.title;
  const last = index >= STEPS.length - 1;

  return (
    <Dialog open={open} title={title} onClose={onDone}>
      <p aria-live="polite">{t("Tour_StepTourIndex1OfTourSteps", [index + 1, STEPS.length])}</p>
      <p>{t(step.body)}</p>
      <div className="home-start-actions">
        <button
          type="button"
          disabled={index === 0}
          aria-label={t("Tour_BackToThePreviousStep")}
          onClick={() => setIndex((value) => Math.max(0, value - 1))}
        >
          {t("Tour_Back")}
        </button>
        <button type="button" aria-label={t("Tour_SkipTheTutorial")} onClick={onDone}>
          {t("Tour_Skip")}
        </button>
        <button
          type="button"
          className="primary-action"
          aria-label={t("Tour_NextStep")}
          onClick={() => {
            if (last) onDone();
            else setIndex((value) => value + 1);
          }}
        >
          {t("Tour_Next")}
        </button>
      </div>
    </Dialog>
  );
}
