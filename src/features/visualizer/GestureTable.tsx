import { useI18n } from "../../i18n";
import {
  summaryActionText,
  type GestureSummary,
} from "./deviceSummary";

export interface GestureTableProps {
  readonly rows: readonly GestureSummary[];
  readonly liveRows?: ReadonlySet<number>;
}

/** Avalonia GestureTable: read-only mini-table inside a zone callout. */
export function GestureTable({ rows, liveRows }: GestureTableProps) {
  const { t, plural } = useI18n();

  return (
    <div className="gesture-table" role="table" aria-label={t("Main_InputsSipsPuffsJoystick")}>
      <div className="gesture-table-rule" aria-hidden="true" />
      {rows.map((summary) => {
        const spoken = summaryActionText(
          summary,
          (count) => plural("Count_Action", count, [count]),
          t("Main_Sequence"),
        );
        const lit = summary.actions.some((action) => liveRows?.has(action.row));
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
            >
              {spoken}
            </span>
          </div>
        );
      })}
    </div>
  );
}
