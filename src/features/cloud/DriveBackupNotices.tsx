import { useEffect, useRef, useState } from "react";

import { Dialog } from "../../components/primitives/Dialog";
import { useI18n } from "../../i18n";
import { localizedErrorMessage } from "../../i18n/errors";
import {
  asQcmError,
  type DriveBackupEvent,
  type DriveConflictChoice,
  type QcmClient,
  type WorkbookImportReview,
} from "../../platform";

interface DriveBackupNoticesProps {
  readonly client: QcmClient;
  /** The status line (`Status`), for pushed / pending. */
  readonly onMessage: (message: string) => void;
  /** Keep online: the sheet comes back as an import to review. */
  readonly onReview: (review: WorkbookImportReview) => void;
}

type Question = Extract<DriveBackupEvent, { kind: "conflict" | "missing" }>;

/**
 * `RunBackup`: the push every save hands to Drive in the background reports
 * here. A conflict or a missing sheet asks once, with the shipped app's
 * Yes / Cancel; Cancel is the default, as in `ConfirmAsync`.
 */
export function DriveBackupNotices({ client, onMessage, onReview }: DriveBackupNoticesProps) {
  const { t } = useI18n();
  // One question at a time, in the order the pushes asked: a second
  // profile's conflict waits its turn instead of taking the first one's place.
  const [questions, setQuestions] = useState<readonly Question[]>([]);
  const question = questions[0] ?? null;
  const [busy, setBusy] = useState(false);
  const say = useRef(onMessage);
  const review = useRef(onReview);
  const tr = useRef(t);
  useEffect(() => {
    say.current = onMessage;
    review.current = onReview;
    tr.current = t;
  });

  const subscribe = client.subscribeDriveBackups;
  useEffect(() => {
    if (subscribe === undefined) return undefined;
    let disposed = false;
    let dispose: (() => void) | null = null;
    const voided = new Set<string>();
    void subscribe.call(client, (event) => {
      if (disposed) return;
      if (event.kind === "pushed") say.current(tr.current("Main_BackedUpToGoogleDrive"));
      else if (event.kind === "pending") say.current(tr.current("Backup_BackupPending"));
      else if (event.kind === "superseded") {
        // It can overtake the question it voids, which then never shows.
        voided.add(event.resolutionId);
        if (voided.size > 256) voided.delete(voided.values().next().value ?? "");
        setQuestions((queued) => queued.filter((q) => q.resolutionId !== event.resolutionId));
      } else if (!voided.delete(event.resolutionId)) setQuestions((queued) => [...queued, event]);
    }).then((subscription) => {
      if (disposed) subscription.dispose();
      else dispose = () => subscription.dispose();
    }, () => undefined);
    return () => {
      disposed = true;
      dispose?.();
    };
  }, [client, subscribe]);

  const answer = async (choice: DriveConflictChoice): Promise<void> => {
    const resolve = client.resolveDriveConflict;
    if (question === null || resolve === undefined || busy) return;
    setBusy(true);
    try {
      const result = await resolve.call(client, question.resolutionId, choice);
      setQuestions((queued) => queued.slice(1));
      if (result.kind === "review") review.current(result.review);
      else if (result.result.kind === "pushed") say.current(t("Main_BackedUpToGoogleDrive"));
      else if (result.result.kind === "disabled") say.current(t("Backup_BackupWasTurnedOffFor"));
    } catch (reason) {
      setQuestions((queued) => queued.slice(1));
      say.current(localizedErrorMessage(asQcmError(reason).payload, t));
    } finally {
      setBusy(false);
    }
  };

  if (question === null) return null;
  const conflict = question.kind === "conflict";
  const no: DriveConflictChoice = conflict ? "keep_online" : "disable";
  return (
    <Dialog
      open
      title={conflict ? t("Backup_SheetEditedOnline") : t("Backup_BackupSheetNotFound")}
      onClose={() => void answer(no)}
      actions={
        <>
          <button type="button" disabled={busy} onClick={() => void answer(conflict ? "replace_with_mine" : "recreate")}>
            {t("Main_YesContinue")}
          </button>
          <button className="primary" type="button" data-autofocus disabled={busy} onClick={() => void answer(no)}>
            {t("Main_Cancel")}
          </button>
        </>
      }
    >
      <p>{conflict ? t("Backup_ThisProfileSGoogleSheet") : t("Backup_TheGoogleSheetForThis")}</p>
    </Dialog>
  );
}
