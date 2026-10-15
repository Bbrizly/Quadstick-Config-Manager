import { useEffect, useState } from "react";

import { Dialog } from "../../components/primitives/Dialog";
import { LiveRegion } from "../../components/primitives/LiveRegion";
import { useI18n } from "../../i18n";
import { localizedErrorMessage } from "../../i18n/errors";
import {
  asQcmError,
  type DriveFile,
  type QcmClient,
  type RestoreReason,
  type RestoreSummary,
  type WorkbookImportReview,
} from "../../platform";

export interface DrivePickerDialogProps {
  readonly client: QcmClient;
  readonly onReview: (review: WorkbookImportReview) => void;
  readonly onClose: () => void;
  /** Restore-all after a first sign-in: every importable backup starts ticked. */
  readonly preCheck?: boolean;
}

type Translate = ReturnType<typeof useI18n>["t"];

const REASON_KEYS = {
  already_exists: "Backup_AlreadyExists",
  could_not_read_the_sheet: "Backup_CouldNotReadTheSheet2",
  not_a_valid_profile: "Backup_NotAValidProfile",
  download_failed: "Backup_DownloadFailed",
  could_not_write_the_file: "Backup_CouldNotWriteTheFile",
  could_not_save_the_link: "Backup_CouldNotSaveTheLink",
} as const satisfies Record<RestoreReason, string>;

/** `RestoreSummary.Message`: "3 imported, 1 skipped: mygame already exists". */
export function restoreSummaryText(summary: RestoreSummary, t: Translate): string {
  const parts = [t("Drive_RestoreImported", [summary.imported.length])];
  const named = (items: RestoreSummary["skipped"]): string =>
    items.map((item) => `${item.name} ${t(REASON_KEYS[item.reason])}`).join(", ");
  if (summary.skipped.length > 0) {
    parts.push(t("Drive_RestoreSkipped", [summary.skipped.length]) + named(summary.skipped));
  }
  if (summary.failed.length > 0) {
    parts.push(t("Drive_RestoreFailed", [summary.failed.length]) + named(summary.failed));
  }
  return parts.join(", ");
}

/** RFC 3339 from Drive as a local short date, or the raw text (`ShortDate`). */
export function shortDate(modifiedTime: string, locale?: string): string {
  const time = Date.parse(modifiedTime);
  if (Number.isNaN(time)) return modifiedTime;
  try {
    return new Date(time).toLocaleDateString(locale);
  } catch {
    return new Date(time).toLocaleDateString();
  }
}

/**
 * `DrivePickerWindow.cs`: the backups load after the dialog opens, so a Drive
 * failure is a status line and not a broken page. With bulk restore the picks
 * are copied into the profile folder and linked (`DriveBackup.RestoreAsync`);
 * a client without it restores one backup into the import review.
 */
export function DrivePickerDialog({ client, onReview, onClose, preCheck = false }: DrivePickerDialogProps) {
  const { t, locale } = useI18n();
  const [files, setFiles] = useState<readonly DriveFile[]>([]);
  const [picked, setPicked] = useState<ReadonlySet<string>>(new Set());
  const bulk = client.restoreDriveBackups;
  const [status, setStatus] = useState(t("DrivePick_LoadingYourBackups"));
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    const list = client.listDriveBackups;
    if (list === undefined) return undefined;
    let active = true;
    void list.call(client).then(
      (next) => {
        if (!active) return;
        setFiles(next);
        if (preCheck) {
          setPicked(new Set(next.filter((file) => file.alreadyLinked !== true).map((file) => file.cloudRef)));
        }
        setStatus(next.length === 0 ? t("DrivePick_NoBackupsFoundInYour") : "");
      },
      (reason: unknown) => {
        if (active) {
          setStatus(t("DrivePick_CouldNotLoadYourBackups") + localizedErrorMessage(asQcmError(reason).payload, t));
        }
      },
    );
    return () => {
      active = false;
    };
  }, [client, preCheck, t]);

  const importPicked = async (): Promise<void> => {
    const restore = client.restoreDriveBackup;
    if ((bulk === undefined && restore === undefined) || busy) return;
    const chosen = files.filter((file) => picked.has(file.cloudRef)).map((file) => file.cloudRef);
    if (chosen.length === 0) {
      setStatus(t("DrivePick_NothingSelectedToImport"));
      return;
    }
    setBusy(true);
    setStatus(t("DrivePick_Importing"));
    try {
      if (bulk !== undefined) {
        const summary = await bulk.call(client, chosen);
        // The window stays, with what happened, as the shipped picker does.
        setPicked(new Set());
        setStatus(restoreSummaryText(summary, t));
        return;
      }
      if (restore === undefined) return;
      const review = await restore.call(client, chosen[0]!);
      onReview(review);
      onClose();
    } catch (reason) {
      setStatus(t("DrivePick_ImportFailed") + localizedErrorMessage(asQcmError(reason).payload, t));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog
      open
      title={t("DrivePick_ImportFromGoogleDrive")}
      // Not while an import runs: its result has to reach the review.
      onClose={() => {
        if (!busy) onClose();
      }}
      actions={
        <>
          <button
            className="primary-action"
            type="button"
            disabled={busy || files.length === 0}
            aria-label={t("DrivePick_ImportTheSelectedProfiles")}
            onClick={() => void importPicked()}
          >
            {t("DrivePick_Import")}
          </button>
          <button
            type="button"
            data-autofocus
            disabled={busy}
            aria-label={t("DrivePick_CloseThisWindow")}
            onClick={onClose}
          >
            {t("DrivePick_Close")}
          </button>
        </>
      }
    >
      <h3>{t("DrivePick_YourGoogleDriveBackups")}</h3>
      <p>{t("DrivePick_PickTheProfilesToCopy")}</p>
      {files.length === 0 ? null : (
        <fieldset className="drive-backup-list">
          <legend className="visually-hidden">{t("DrivePick_YourGoogleDriveBackups")}</legend>
          {files.map((file) => {
            const linked = file.alreadyLinked === true;
            const label = `${file.name}, ${shortDate(file.modifiedTime, locale)}${linked ? t("DrivePick_AlreadyInYourProfiles") : ""}`;
            return (
              <label key={file.cloudRef} className="checkbox-row">
                {bulk === undefined ? (
                  <input
                    type="radio"
                    name="drive-backup"
                    checked={picked.has(file.cloudRef)}
                    disabled={busy}
                    onChange={() => setPicked(new Set([file.cloudRef]))}
                  />
                ) : (
                  <input
                    type="checkbox"
                    checked={picked.has(file.cloudRef)}
                    disabled={busy || linked}
                    onChange={(event) => {
                      const on = event.currentTarget.checked;
                      setPicked((before) => {
                        const next = new Set(before);
                        if (on) next.add(file.cloudRef);
                        else next.delete(file.cloudRef);
                        return next;
                      });
                    }}
                  />
                )}
                <span>{label}</span>
              </label>
            );
          })}
        </fieldset>
      )}
      <LiveRegion visuallyHidden={false}>{status}</LiveRegion>
    </Dialog>
  );
}
