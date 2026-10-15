import { useCallback, useEffect, useState } from "react";
import { createPortal } from "react-dom";

import { LiveRegion } from "../../components/primitives/LiveRegion";
import { RunOnce } from "../../components/primitives/RunOnce";
import { DrivePickerDialog } from "./DrivePickerDialog";
import { RestoreAfterConnect } from "./HomeDriveButton";
import { ShareSetupDialog } from "./ShareSetupDialog";
import { useI18n } from "../../i18n";
import { localizedErrorMessage } from "../../i18n/errors";
import {
  asQcmError,
  type DriveBackupOutcome,
  type DriveConflictChoice,
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
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [picker, setPicker] = useState<{ readonly preCheck: boolean } | null>(null);
  const [askRestore, setAskRestore] = useState(false);

  const status = client.getGoogleAuthStatus;
  const connect = client.connectGoogle;
  const disconnect = client.disconnectGoogle;
  const list = client.listDriveBackups;

  const showFailure = useCallback((reason: unknown): void => {
    setMessage(localizedErrorMessage(asQcmError(reason).payload, t));
  }, [t]);

  useEffect(() => {
    if (status === undefined) return;
    let disposed = false;
    void status.call(client).then((value) => {
      if (!disposed) setAuth(value);
    }).catch((reason: unknown) => {
      if (!disposed) showFailure(reason);
    });
    return () => { disposed = true; };
  }, [client, showFailure, status]);

  if (status === undefined || connect === undefined || disconnect === undefined) return null;

  const connectNow = async (): Promise<void> => {
    if (busy) return;
    setBusy(true);
    setMessage(t("Settings_WaitingBrowser"));
    try {
      const next = await connect.call(client);
      setAuth(next);
      setMessage(next.connected ? t("Settings_Connected") : "");
      // After a first sign-in, offer to bring the backed-up profiles over.
      if (next.connected && list !== undefined) setAskRestore(true);
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
      setMessage("");
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
            {t("Rewrite_TurnOff")}
          </button>
        ) : (
          <button type="button" disabled={busy || auth?.configured === false || auth?.supported === false} onClick={() => void connectNow()}>
            {t("Settings_Reconnect")}
          </button>
        )}
        {auth?.connected && list !== undefined ? (
          <button
            type="button"
            disabled={busy}
            aria-label={t("Settings_ImportDriveHelp")}
            onClick={() => setPicker({ preCheck: false })}
          >
            {t("Settings_ImportDrive")}
          </button>
        ) : null}
      </div>
      <RestoreAfterConnect
        open={askRestore}
        onAnswer={(yes) => {
          setAskRestore(false);
          if (yes) setPicker({ preCheck: true });
        }}
      />
      {picker === null ? null : (
        <DrivePickerDialog
          client={client}
          preCheck={picker.preCheck}
          onReview={onReview}
          onClose={() => setPicker(null)}
        />
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
  /** Where the buttons go (the editor's Share flyout); `null` while it is shut. */
  readonly buttonsSlot?: HTMLElement | null;
  /** A Home card's Copy share link / Open in Google Sheets, run once. */
  readonly shareRequest?: { readonly n: number; readonly open: boolean } | undefined;
}

export function ProfileDriveActions({
  client,
  snapshot,
  onSnapshot,
  onReview,
  buttonsSlot,
  shareRequest,
}: ProfileDriveActionsProps) {
  const { t } = useI18n();
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [conflict, setConflict] = useState<DriveBackupOutcome | null>(null);
  const [shareUrl, setShareUrl] = useState("");
  // `ReadyForDriveAsync`: sharing without a Google connection walks the
  // setup steps first, then runs the share the person asked for.
  const [setup, setSetup] = useState<{
    readonly needsSave: boolean;
    readonly connected: boolean;
    readonly done: (completed: boolean) => void;
  } | null>(null);

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

  const readyForDrive = async (): Promise<boolean> => {
    const status = client.getGoogleAuthStatus;
    if (status === undefined) return true;
    const auth = await status.call(client);
    if (auth.connected) return true;
    return new Promise<boolean>((settle) => {
      setSetup({
        needsSave: snapshot.saveTarget === null,
        connected: false,
        done: (completed) => {
          setSetup(null);
          settle(completed);
        },
      });
    });
  };

  const shareNow = async (open = false): Promise<void> => {
    if (share === undefined || backup === undefined || busy) return;
    setBusy(true);
    try {
      if (!(await readyForDrive())) return;
      const current = await ensureSaved();
      if (current === null) return;
      const backedUp = await backup.call(client, current.sessionId, current.revision);
      if (backedUp.kind === "conflict" || backedUp.kind === "missing") {
        handleOutcome(backedUp);
        return;
      }
      const shared = await share.call(client, current.sessionId, current.revision);
      if (open && client.openExternalUrl !== undefined) {
        await client.openExternalUrl(shared.url);
        return;
      }
      setShareUrl(shared.url);
      setMessage(t("Backup_LinkCopied"));
    } catch (reason) {
      showFailure(reason);
    } finally {
      setBusy(false);
    }
  };

  if (backup === undefined) return null;

  // `ShareMenu`: copy the link, open the sheet; a backup now as well.
  const buttons = (
    <>
      {share === undefined ? null : (
        <button type="button" className="menu-item" disabled={busy} aria-label={t("Main_CopyALinkToThis")} onClick={() => void shareNow()}>
          {t("Main_CopyShareLink")}
        </button>
      )}
      {share === undefined || client.openExternalUrl === undefined ? null : (
        <button type="button" className="menu-item" disabled={busy} aria-label={t("Main_OpenThisProfileSGoogle")} onClick={() => void shareNow(true)}>
          {t("Main_OpenInGoogleSheets")}
        </button>
      )}
      <button type="button" className="menu-item" disabled={busy} onClick={() => void backupNow()}>
        {t("Main_BackingUpToGoogleDrive")}
      </button>
    </>
  );

  return (
    <section className="profile-drive-actions" aria-label={t("Settings_Backup")}>
      <RunOnce request={shareRequest} ready onRun={(next) => void shareNow(next.open)} />
      {buttonsSlot === undefined ? buttons : buttonsSlot === null ? null : createPortal(buttons, buttonsSlot)}

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
            {t("Rewrite_TurnOff")}
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
      {setup === null ? null : (
        <ShareSetupDialog
          client={client}
          snapshot={snapshot}
          finishLabel={t("Main_CopyShareLink")}
          needsSave={setup.needsSave}
          connected={setup.connected}
          onConnected={() => setSetup({ ...setup, connected: true })}
          onSnapshot={onSnapshot}
          onClose={setup.done}
        />
      )}
    </section>
  );
}
