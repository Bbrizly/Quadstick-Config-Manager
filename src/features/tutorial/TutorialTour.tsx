import { useState } from "react";

import { Dialog } from "../../components/primitives/Dialog";
import { useI18n, type MessageKey } from "../../i18n";

/** Soft tour: callout steps only. Spotlight targeting lands with AT hardening. */
// Avalonia hard-codes five of these titles in English; here every one is a
// catalog word, so a French or Arabic tour reads in that language throughout.
const STEPS: readonly { title: MessageKey; body: MessageKey }[] = [
  { title: "Rewrite_TourWelcome", body: "Tour_ThisShowsYouHowTo" },
  { title: "Settings_Appearance", body: "Tour_SetLightOrDarkTo" },
  { title: "Tour_NewProfile", body: "Tour_EveryProfileStartsFromA" },
  { title: "Rewrite_TourYourQuadStick", body: "Tour_ThisIsYourQuadStickEach" },
  { title: "Tour_PickAPart", body: "Tour_PickAPartToSee" },
  { title: "Main_Save", body: "Tour_SaveYourWorkToA" },
  { title: "Rewrite_TourInstall", body: "Tour_WhenItSReadySend" },
  { title: "Main_Done", body: "Tour_YouCanReplayThisAnytime" },
];

export interface TutorialTourProps {
  readonly open: boolean;
  readonly onDone: () => void;
}

export function TutorialTour({ open, onDone }: TutorialTourProps) {
  const { t } = useI18n();
  const [index, setIndex] = useState(0);
  const step = STEPS[index] ?? STEPS[0]!;
  const title = t(step.title);
  const last = index >= STEPS.length - 1;
  // Closed, it stays while it fades. Nothing pressed then counts: a second
  // finish would save the settings again on a spent revision.
  const finish = (): void => {
    if (open) onDone();
  };

  return (
    <Dialog open={open} title={title} onClose={finish}>
      <p aria-live="polite">{t("Tour_StepTourIndex1OfTourSteps", [index + 1, STEPS.length])}</p>
      <p>{t(step.body)}</p>
      <div className="home-start-actions">
        <button
          type="button"
          disabled={index === 0}
          aria-label={t("Tour_BackToThePreviousStep")}
          onClick={() => {
            if (open) setIndex((value) => Math.max(0, value - 1));
          }}
        >
          {t("Tour_Back")}
        </button>
        <button type="button" aria-label={t("Tour_SkipTheTutorial")} onClick={finish}>
          {t("Tour_Skip")}
        </button>
        <button
          type="button"
          className="primary-action"
          aria-label={t("Tour_NextStep")}
          onClick={() => {
            if (!open) return;
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
