import { useI18n } from "../../i18n";
import {
  summaryActionText,
  type GestureSummary,
} from "./deviceSummary";
import { OutputPrompt, hasPromptArt, requiresTextLabel } from "./OutputPrompt";

export interface GestureTableProps {
  readonly rows: readonly GestureSummary[];
  readonly liveRows?: ReadonlySet<number>;
}

/** Avalonia GestureTable: name | 10px gutter with pip | action. */
export function GestureTable({ rows, liveRows }: GestureTableProps) {
  const { t, plural } = useI18n();

  return (
    <div className="gesture-table" role="table" aria-label={t("Main_InputsSipsPuffsJoystick")}>
      {rows.map((summary, index) => {
        const spoken = summaryActionText(
          summary,
          (count) => plural("Count_Action", count, [count]),
          t("Main_Sequence"),
        );
        const lit = summary.actions.some((action) => liveRows?.has(action.row));
        const named = summary.actions.filter(
          (action) => !action.isSupport && action.friendlyOutput.length > 0,
        );
        const showArt =
          !summary.hasComplexBehavior &&
          named.length > 0 &&
          named.length <= 4 &&
          named.every(
            (action) =>
              !action.hasCustomName &&
              !requiresTextLabel(action.output) &&
              hasPromptArt(action.output),
          );

        return (
          <div
            className={lit ? "gesture-row live" : "gesture-row"}
            role="row"
            key={`${summary.zone}-${summary.inputToken}`}
            data-live={lit ? "true" : undefined}
          >
            {index > 0 ? <span className="gesture-rule" aria-hidden="true" /> : null}
            <span className="gesture-name" role="cell">
              {t(summary.friendlyGestureKey)}
            </span>
            <span className="gesture-gutter-cell" aria-hidden="true">
              {lit ? (
                <span className="gesture-pip" role="img" aria-label={t("Main_SendingNow")} />
              ) : summary.isMapped ? (
                <span className="gesture-pip-slot" />
              ) : null}
            </span>
            <span
              className={summary.isMapped ? "gesture-action" : "gesture-action muted"}
              role="cell"
              aria-label={spoken}
            >
              {showArt ? (
                <span className="gesture-prompts">
                  {named.map((action, artIndex) => (
                    <span className="gesture-prompt-item" key={`${action.row}-${action.output}`}>
                      {artIndex > 0 ? <span className="gesture-dot" aria-hidden="true">·</span> : null}
                      <OutputPrompt token={action.output} label={action.friendlyOutput} size={30} />
                    </span>
                  ))}
                </span>
              ) : (
                spoken
              )}
            </span>
          </div>
        );
      })}
    </div>
  );
}
