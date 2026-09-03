import { useCallback, useEffect, useState } from "react";

import { LiveRegion } from "../../components/primitives/LiveRegion";
import { useI18n } from "../../i18n";
import { localizedErrorMessage } from "../../i18n/errors";
import {
  asQcmError,
  type DriveBackupOutcome,
  type DriveConflictChoice,
  type DriveFile,
  type EditorSnapshot,
  type GoogleAuthStatus,
  type QcmClient,
  type WorkbookImportReview,
} from "../../platform";

interface GoogleDriveSettingsProps {
  readonly client: QcmClient;
  readonly onReview: (review: WorkbookImportReview) => void;
}

export function GoogleDriveSettings({ client, onReview }: GoogleDriveSettingsProps) {
  const { t } = useI18n();
  const [auth, setAuth] = useState<GoogleAuthStatus | null>(null);
  const [files, setFiles] = useState<readonly DriveFile[]>([]);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");

  const status = client.getGoogleAuthStatus;
  const connect = client.connectGoogle;
  const disconnect = client.disconnectGoogle;
  const list = client.listDriveBackups;
  const restore = client.restoreDriveBackup;

  const showFailure = useCallback((reason: unknown): void => {
    setMessage(localizedErrorMessage(asQcmError(reason).payload, t));
  }, [t]);

  const refreshFiles = useCallback(async (): Promise<void> => {
    if (list === undefined) return;
    try {
      setFiles(await list.call(client));
    } catch (reason) {
      showFailure(reason);
    }
  }, [client, list, showFailure]);

  useEffect(() => {
    if (status === undefined) return;
    let disposed = false;
    void status.call(client).then((value) => {
      if (!disposed) setAuth(value);
      if (!disposed && value.connected) void refreshFiles();
    }).catch((reason: unknown) => {
      if (!disposed) showFailure(reason);
    });
    return () => { disposed = true; };
  }, [client, refreshFiles, showFailure, status]);

  if (status === undefined || connect === undefined || disconnect === undefined) return null;

  const connectNow = async (): Promise<void> => {
    if (busy) return;
    setBusy(true);
    setMessage(t("Settings_WaitingBrowser"));
    try {
      const next = await connect.call(client);
      setAuth(next);
      setMessage(next.connected ? t("Settings_Connected") : "");
      if (next.connected) await refreshFiles();
    } catch (reason) {
      showFailure(reason);
    } finally {
      setBusy(false);
    }
  };

  const disconnectNow = async (): Promise<void> => {
    if (busy) return;
    setBusy(true);
    try {
      setAuth(await disconnect.call(client));
      setFiles([]);
      setMessage("");
    } catch (reason) {
      showFailure(reason);
    } finally {
      setBusy(false);
    }
  };

  const restoreOne = async (file: DriveFile): Promise<void> => {
    if (restore === undefined || busy) return;
    setBusy(true);
    setMessage(t("DrivePick_Importing"));
    try {
      const review = await restore.call(client, file.cloudRef);
      setMessage("");
      onReview(review);
    } catch (reason) {
      showFailure(reason);
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="settings-drive" aria-labelledby="settings-drive-title">
      <h3 id="settings-drive-title">{t("Settings_Backup")}</h3>
      <p>{auth?.configured === false ? t("Settings_BackupUnavailable") : t("Settings_BackupCaption")}</p>
      {auth?.connected ? <p>{t("Settings_Connected")}</p> : null}
      <div className="settings-drive-actions">
        {auth?.connected ? (
          <button type="button" disabled={busy} onClick={() => void disconnectNow()}>
            {t("Settings_TurnOff")}
          </button>
        ) : (
          <button type="button" disabled={busy || auth?.configured === false || auth?.supported === false} onClick={() => void connectNow()}>
            {t("Settings_Reconnect")}
          </button>
        )}
        {auth?.connected && list !== undefined ? (
          <button type="button" disabled={busy} onClick={() => void refreshFiles()}>
            {t("Settings_ImportDrive")}
          </button>
        ) : null}
      </div>

      {auth?.connected && files.length === 0 ? <p>{t("DrivePick_NoBackupsFoundInYour")}</p> : null}
      {files.length === 0 ? null : (
        <ul className="drive-backup-list" aria-label={t("DrivePick_YourGoogleDriveBackups")}>
          {files.map((file) => (
            <li key={file.cloudRef}>
              <span>{file.name}</span>
              <button type="button" disabled={busy} onClick={() => void restoreOne(file)}>
                {t("DrivePick_Import")}
              </button>
            </li>
          ))}
        </ul>
      )}
      <LiveRegion>{message}</LiveRegion>
    </section>
  );
}

interface ProfileDriveActionsProps {
  readonly client: QcmClient;
  readonly snapshot: EditorSnapshot;
  readonly onSnapshot: (snapshot: EditorSnapshot) => void;
  readonly onReview: (review: WorkbookImportReview) => void;
}

export function ProfileDriveActions({
  client,
  snapshot,
  onSnapshot,
  onReview,
}: ProfileDriveActionsProps) {
  const { t } = useI18n();
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [conflict, setConflict] = useState<DriveBackupOutcome | null>(null);
  const [shareUrl, setShareUrl] = useState("");

  const backup = client.backupProfileToDrive;
  const resolve = client.resolveDriveConflict;
  const share = client.shareDriveProfile;

  const showFailure = (reason: unknown): void => {
    setMessage(localizedErrorMessage(asQcmError(reason).payload, t));
  };

  const ensureSaved = async (): Promise<EditorSnapshot | null> => {
    let current = snapshot;
    if (current.saveTarget === null) {
      const receipt = await client.saveProfileAs(current.sessionId, current.revision);
      if (receipt === null) return null;
      current = await client.getProfileSnapshot(current.sessionId);
      onSnapshot(current);
    } else if (current.dirty) {
      await client.saveProfile(current.sessionId, current.revision);
      current = await client.getProfileSnapshot(current.sessionId);
      onSnapshot(current);
    }
    return current;
  };

  const handleOutcome = (outcome: DriveBackupOutcome): void => {
    if (outcome.kind === "conflict" || outcome.kind === "missing") {
      setConflict(outcome);
      setMessage(outcome.kind === "conflict" ? t("Backup_SheetEditedOnline") : t("Backup_BackupSheetNotFound"));
      return;
    }
    setConflict(null);
    setMessage(outcome.kind === "pushed" ? t("Main_BackedUpToGoogleDrive") : t("Backup_BackupWasTurnedOffFor"));
  };

  const backupNow = async (): Promise<void> => {
    if (backup === undefined || busy) return;
    setBusy(true);
    setShareUrl("");
    setMessage(t("Main_BackingUpToDrive"));
    try {
      const current = await ensureSaved();
      if (current === null) return;
      handleOutcome(await backup.call(client, current.sessionId, current.revision));
    } catch (reason) {
      showFailure(reason);
    } finally {
      setBusy(false);
    }
  };

  const resolveNow = async (choice: DriveConflictChoice): Promise<void> => {
    if (resolve === undefined || conflict === null || (conflict.kind !== "conflict" && conflict.kind !== "missing") || busy) return;
    setBusy(true);
    try {
      const result = await resolve.call(client, conflict.resolutionId, choice);
      setConflict(null);
      if (result.kind === "review") {
        setMessage("");
        onReview(result.review);
      } else {
        handleOutcome(result.result);
      }
    } catch (reason) {
      showFailure(reason);
    } finally {
      setBusy(false);
    }
  };

  const shareNow = async (): Promise<void> => {
    if (share === undefined || backup === undefined || busy) return;
    setBusy(true);
    try {
      const current = await ensureSaved();
      if (current === null) return;
      const backedUp = await backup.call(client, current.sessionId, current.revision);
      if (backedUp.kind === "conflict" || backedUp.kind === "missing") {
        handleOutcome(backedUp);
        return;
      }
      const shared = await share.call(client, current.sessionId, current.revision);
      setShareUrl(shared.url);
      setMessage(t("Backup_LinkCopied"));
    } catch (reason) {
      showFailure(reason);
    } finally {
      setBusy(false);
    }
  };

  if (backup === undefined) return null;

  return (
    <section className="profile-drive-actions" aria-label={t("Settings_Backup")}>
      <button type="button" disabled={busy} onClick={() => void backupNow()}>
        {t("Main_BackingUpToGoogleDrive")}
      </button>
      {share === undefined ? null : (
        <button type="button" disabled={busy} onClick={() => void shareNow()}>
          {t("Main_CopyShareLink")}
        </button>
      )}

      {conflict?.kind === "conflict" ? (
        <div className="drive-conflict" role="alert">
          <p>{t("Backup_ThisProfileSGoogleSheet")}</p>
          <button type="button" disabled={busy} onClick={() => void resolveNow("replace_with_mine")}>
            {t("Main_Save")}
          </button>
          <button type="button" disabled={busy} onClick={() => void resolveNow("keep_online")}>
            {t("DrivePick_Import")}
          </button>
        </div>
      ) : null}

      {conflict?.kind === "missing" ? (
        <div className="drive-conflict" role="alert">
          <p>{t("Backup_TheGoogleSheetForThis")}</p>
          <button type="button" disabled={busy} onClick={() => void resolveNow("recreate")}>
            {t("Shell_SaveCtrlS")}
          </button>
          <button type="button" disabled={busy} onClick={() => void resolveNow("disable")}>
            {t("Settings_TurnOff")}
          </button>
        </div>
      ) : null}

      {shareUrl.length === 0 ? null : (
        <label>
          <span>{t("Main_CopyShareLink")}</span>
          <input readOnly value={shareUrl} onFocus={(event) => event.currentTarget.select()} />
        </label>
      )}
      <LiveRegion>{message}</LiveRegion>
    </section>
  );
}
