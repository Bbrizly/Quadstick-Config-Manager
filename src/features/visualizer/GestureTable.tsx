import { useI18n } from "../../i18n";
import {
  summaryActionText,
  type GestureSummary,
} from "./deviceSummary";
import { OutputPrompt, promptSrc, requiresTextLabel } from "./OutputPrompt";

export interface GestureTableProps {
  readonly rows: readonly GestureSummary[];
  readonly liveRows?: ReadonlySet<number>;
}

/** Avalonia GestureTable: read-only mini-table inside a zone callout. */
export function GestureTable({ rows, liveRows }: GestureTableProps) {
  const { t, plural } = useI18n();

  return (
    <div className="gesture-table" role="table" aria-label={t("Main_InputsSipsPuffsJoystick")}>
      <div className="gesture-gutter" aria-hidden="true" />
      {rows.map((summary) => {
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
              promptSrc(action.output) !== null,
          );

        return (
          <div
            className={lit ? "gesture-row live" : "gesture-row"}
            role="row"
            key={`${summary.zone}-${summary.inputToken}`}
            data-live={lit ? "true" : undefined}
          >
            {lit ? (
              <span className="gesture-pip" role="img" aria-label={t("Main_SendingNow")} />
            ) : (
              <span className="gesture-pip-slot" aria-hidden="true" />
            )}
            <span className="gesture-name" role="cell">
              {t(summary.friendlyGestureKey)}
            </span>
            <span
              className={summary.isMapped ? "gesture-action" : "gesture-action muted"}
              role="cell"
              aria-label={spoken}
            >
              {showArt ? (
                <span className="gesture-prompts">
                  {named.map((action, index) => (
                    <span className="gesture-prompt-item" key={`${action.row}-${action.output}`}>
                      {index > 0 ? <span className="gesture-dot" aria-hidden="true">·</span> : null}
                      <OutputPrompt token={action.output} label={action.friendlyOutput} size={20} />
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
