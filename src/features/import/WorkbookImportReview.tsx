import { Dialog } from "../../components/primitives/Dialog";
import { useI18n, type MessageKey } from "../../i18n";
import type { WorkbookImportReview, WorkbookLimitation } from "../../platform/workbookContracts";

interface WorkbookImportReviewProps {
  readonly review: WorkbookImportReview | null;
  readonly busy: boolean;
  readonly onRepair: (tabIndex: number) => void;
  readonly onAccept: () => void;
  readonly onCancel: () => void;
}

export function WorkbookImportReviewDialog({
  review,
  busy,
  onRepair,
  onAccept,
  onCancel,
}: WorkbookImportReviewProps) {
  const { plural, t } = useI18n();
  if (review === null) return null;

  const lost = review.skipped.filter((tab) => tab.kind === "unreadable_a1");
  const helpers = review.skipped.filter((tab) => tab.kind === "helper");
  const clean = review.limitation === null && lost.length === 0 && review.errorCount === 0;

  return (
    <Dialog
      open
      title={t("Review_ImportReview")}
      onClose={onCancel}
      actions={
        <>
          <button type="button" disabled={busy} onClick={onCancel}>
            {t("Device_Cancel")}
          </button>
          <button
            className="primary-action"
            type="button"
            data-autofocus
            disabled={busy}
            onClick={onAccept}
          >
            {t("Community_Import")}
          </button>
        </>
      }
    >
      <div className="workbook-review">
        <h2>{clean ? t("Review_YourSheetCameInClean") : t("Review_WeReadYourSheet")}</h2>
        <p>{review.name}</p>

        {review.limitation !== null ? (
          <section aria-labelledby="workbook-limit-heading">
            <h3 id="workbook-limit-heading">{t("Review_OnlyPartOfTheSpreadsheet")}</h3>
            <p>{limitationText(review.limitation, t)}</p>
          </section>
        ) : null}

        {review.errorCount > 0 || review.warningCount > 0 ? (
          <section aria-label={t("Shell_ListOfValidationProblemsSelect")}>
            <p>
              {plural("Count_Error", review.errorCount, [review.errorCount])}
              {" · "}
              {plural("Count_Warning", review.warningCount, [review.warningCount])}
            </p>
          </section>
        ) : null}

        {lost.length > 0 ? (
          <section aria-labelledby="workbook-lost-heading">
            <h3 id="workbook-lost-heading">
              {lost.length === 1 ? t("Review_TabDidNotComeIn") : t("Review_TabsDidNotComeIn")}
            </h3>
            <ul className="workbook-review-list">
              {lost.map((tab) => (
                <li key={`${tab.index}-${tab.name}`}>
                  <strong>{tab.name}</strong>
                  {tab.preview.length > 0 ? (
                    <div className="workbook-preview" role="table" aria-label={tab.name}>
                      {tab.preview.map((row, rowIndex) => (
                        <div role="row" key={rowIndex}>
                          {row.map((cell, columnIndex) => (
                            <span role="cell" key={columnIndex}>{cell}</span>
                          ))}
                        </div>
                      ))}
                    </div>
                  ) : null}
                  {tab.repairable ? (
                    <button
                      type="button"
                      disabled={busy}
                      aria-label={t("Review_AddTheTabTabName", [tab.name])}
                      onClick={() => onRepair(tab.index)}
                    >
                      {t("Review_AddItAsAWorking")}
                    </button>
                  ) : null}
                </li>
              ))}
            </ul>
          </section>
        ) : null}

        {helpers.length > 0 ? (
          <section aria-labelledby="workbook-helper-heading">
            <h3 id="workbook-helper-heading">
              {helpers.length === 1
                ? t("Review_TabIsNotProfileData")
                : t("Review_TabsAreNotProfileData")}
            </h3>
            <p>
              {plural(
                "Review_HelperTabs",
                helpers.length,
                [helpers.map((tab) => `“${tab.name}”`).join(", ")],
              )}
            </p>
          </section>
        ) : null}

        {review.renamed.length > 0 ? (
          <section aria-labelledby="workbook-renamed-heading">
            <h3 id="workbook-renamed-heading">
              {review.renamed.length === 1
                ? t("Review_ModeIsNamedAfterIts")
                : t("Review_ModesAreNamedAfterTheir")}
            </h3>
            <ul>
              {review.renamed.map((rename) => (
                <li key={`${rename.mode_number}-${rename.tab_name}`}>
                  {rename.cell_c1.length === 0
                    ? t("Review_ModeRModeNumberIsCalled", [rename.mode_number, rename.tab_name])
                    : t("Review_ModeRModeNumberIsCalled2", [
                        rename.mode_number,
                        rename.tab_name,
                        rename.cell_c1,
                      ])}
                </li>
              ))}
            </ul>
          </section>
        ) : null}

        <section aria-labelledby="workbook-came-in-heading">
          <h3 id="workbook-came-in-heading">{t("Review_WhatCameIn")}</h3>
          <ul>
            {review.modes.map((mode) => (
              <li key={`${mode.number}-${mode.kind}-${mode.name}`}>
                <strong>{mode.name}</strong>
                {" — "}
                {plural("Count_Binding", mode.bindingCount, [mode.bindingCount])}
              </li>
            ))}
          </ul>
        </section>
      </div>
    </Dialog>
  );
}

type Translate = (key: MessageKey, values?: readonly unknown[]) => string;

function limitationText(limitation: WorkbookLimitation, t: Translate): string {
  switch (limitation.kind) {
    case "sheet_count":
      return t("Sheet_ThisSpreadsheetHasMoreThan", [limitation.max, limitation.max]);
    case "sheet_rows":
      return t("Sheet_TabPastTheRowCap", [limitation.tab, limitation.max]);
    case "workbook_rows": {
      const cause = t("Sheet_WorkbookPastTheRowCap", [limitation.max]);
      const missed =
        limitation.remaining_tabs === null
          ? t("Sheet_EveryTabFromThereOn")
          : limitation.remaining_tabs === 1
            ? t("Sheet_OneMoreTabWas")
            : t("Sheet_LeftMoreTabsWere", [limitation.remaining_tabs]);
      return t("Sheet_CauseMissedNotReadAt", [cause, missed]);
    }
  }
}
