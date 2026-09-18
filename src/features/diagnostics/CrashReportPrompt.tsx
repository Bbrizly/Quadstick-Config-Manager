import { useCallback, useState } from "react";

import { Dialog } from "../../components/primitives/Dialog";
import { useI18n } from "../../i18n";
import type {
  CrashReportChoice,
  PendingCrashReport,
  QcmClient,
} from "../../platform";

export interface CrashReportPromptProps {
  readonly client: QcmClient;
  readonly report: PendingCrashReport;
  readonly onResolved: (statusMessage: string) => void;
  readonly onDismiss: () => void;
}

export function CrashReportPrompt({
  client,
  report,
  onResolved,
  onDismiss,
}: CrashReportPromptProps) {
  const { t } = useI18n();
  const [busy, setBusy] = useState(false);

  const choose = useCallback(
    async (choice: CrashReportChoice): Promise<void> => {
      const resolve = client.resolveCrashReport;
      if (resolve === undefined || busy) return;
      setBusy(true);
      try {
        const result = await resolve.call(client, report.reportId, choice);
        if (choice === "send") {
          onResolved(
            result.sent
              ? t("Main_CrashReportSentThankYou")
              : t("Main_CouldNotSendTheCrash"),
          );
        } else {
          onDismiss();
        }
      } catch {
        if (choice === "send") {
          onResolved(t("Main_CouldNotSendTheCrash"));
        } else {
          onDismiss();
        }
      } finally {
        setBusy(false);
      }
    },
    [busy, client, onDismiss, onResolved, report.reportId, t],
  );

  return (
    <Dialog
      open
      title={t("Main_SendACrashReport")}
      onClose={() => {
        void choose("later");
      }}
      actions={
        <>
          <button
            className="primary-action"
            type="button"
            disabled={busy}
            aria-label={t("Main_SendThisCrashReport")}
            onClick={() => {
              void choose("send");
            }}
          >
            {t("Main_SendReport")}
          </button>
          <button
            type="button"
            data-autofocus
            disabled={busy}
            aria-label={t("Main_NotNowKeepTheReport")}
            onClick={() => {
              void choose("later");
            }}
          >
            {t("Main_NotNow")}
          </button>
          <button
            type="button"
            disabled={busy}
            aria-label={t("Main_StopAskingAboutCrashReports")}
            onClick={() => {
              void choose("never");
            }}
          >
            {t("Main_StopAsking")}
          </button>
        </>
      }
    >
      <p className="crash-report-note">{t("Main_TheAppCrashedLastTime")}</p>
      <textarea
        className="crash-report-details"
        readOnly
        value={report.details}
        aria-label={t("Main_CrashDetailsUsedToBuild")}
      />
    </Dialog>
  );
}
