import { useEffect, useState } from "react";

import { Dialog } from "../../components/primitives/Dialog";
import { LiveRegion } from "../../components/primitives/LiveRegion";
import { useI18n } from "../../i18n";
import { localizedErrorMessage } from "../../i18n/errors";
import { asQcmError, type DriveFile, type QcmClient, type WorkbookImportReview } from "../../platform";

export interface DrivePickerDialogProps {
  readonly client: QcmClient;
  readonly onReview: (review: WorkbookImportReview) => void;
  readonly onClose: () => void;
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
 * failure is a status line and not a broken page. The rewrite restores one
 * backup into the import review rather than into a library folder, so the
 * list picks one.
 */
export function DrivePickerDialog({ client, onReview, onClose }: DrivePickerDialogProps) {
  const { t, locale } = useI18n();
  const [files, setFiles] = useState<readonly DriveFile[]>([]);
  const [picked, setPicked] = useState<string | null>(null);
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
  }, [client, t]);

  const importPicked = async (): Promise<void> => {
    const restore = client.restoreDriveBackup;
    if (restore === undefined || busy) return;
    if (picked === null) {
      setStatus(t("DrivePick_NothingSelectedToImport"));
      return;
    }
    setBusy(true);
    setStatus(t("DrivePick_Importing"));
    try {
      const review = await restore.call(client, picked);
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
          {files.map((file) => (
            <label key={file.cloudRef} className="checkbox-row">
              <input
                type="radio"
                name="drive-backup"
                checked={picked === file.cloudRef}
                disabled={busy}
                onChange={() => setPicked(file.cloudRef)}
              />
              <span>{`${file.name}, ${shortDate(file.modifiedTime, locale)}`}</span>
            </label>
          ))}
        </fieldset>
      )}
      <LiveRegion visuallyHidden={false}>{status}</LiveRegion>
    </Dialog>
  );
}
