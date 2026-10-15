import { useEffect, useRef, useState } from "react";

import { Dialog } from "../../components/primitives/Dialog";
import { useI18n } from "../../i18n";
import { localizedErrorMessage } from "../../i18n/errors";
import { asQcmError, type GoogleAuthStatus, type QcmClient, type WorkbookImportReview } from "../../platform";
import { DrivePickerDialog } from "./DrivePickerDialog";

/** How long a first press stays armed (`_driveArmTimer`, 4 s). */
export const DRIVE_ARM_MS = 4000;

/**
 * `ConfirmRestoreAfterConnectAsync`: the question after a first sign-in. A yes
 * opens the picker with every backup ticked (restore-all).
 */
export function RestoreAfterConnect({
  open,
  onAnswer,
}: {
  readonly open: boolean;
  readonly onAnswer: (yes: boolean) => void;
}) {
  const { t } = useI18n();
  return (
    <Dialog
      open={open}
      title={t("Main_RestoreYourProfiles")}
      onClose={() => onAnswer(false)}
      actions={
        <>
          <button type="button" data-autofocus onClick={() => onAnswer(false)}>
            {t("Device_Cancel")}
          </button>
          <button className="primary" type="button" onClick={() => onAnswer(true)}>
            {t("Main_YesContinue")}
          </button>
        </>
      }
    >
      <p>{t("Main_CopyYourBackedUpProfiles")}</p>
    </Dialog>
  );
}

export interface HomeDriveButtonProps {
  readonly client: QcmClient;
  readonly onReview: (review: WorkbookImportReview) => void;
}

/**
 * Home's Drive button (`OnDriveButtonClick`). Connected: a press opens the
 * picker to bring other profiles over. Not connected: the first press arms,
 * a second within four seconds signs in, then offers to restore. Asking the
 * sign-in state reads the local token store only; nothing waits while idle
 * except the one disarm timer after a first press.
 */
export function HomeDriveButton({ client, onReview }: HomeDriveButtonProps) {
  const { t } = useI18n();
  const [auth, setAuth] = useState<GoogleAuthStatus | null>(null);
  const [armed, setArmed] = useState(false);
  const [busy, setBusy] = useState(false);
  const [ask, setAsk] = useState(false);
  const [picker, setPicker] = useState<{ readonly preCheck: boolean } | null>(null);
  const [message, setMessage] = useState("");
  const disarm = useRef<ReturnType<typeof setTimeout> | null>(null);

  const status = client.getGoogleAuthStatus;
  const connect = client.connectGoogle;

  useEffect(() => {
    if (status === undefined) return undefined;
    let cancelled = false;
    void status.call(client).then(
      (value) => {
        if (!cancelled) setAuth(value);
      },
      () => undefined,
    );
    return () => {
      cancelled = true;
    };
  }, [client, status]);

  useEffect(
    () => () => {
      if (disarm.current !== null) clearTimeout(disarm.current);
    },
    [],
  );

  if (status === undefined || connect === undefined || client.listDriveBackups === undefined) return null;
  if (auth === null || !auth.supported || !auth.configured) return null;

  const press = async (): Promise<void> => {
    if (busy) return;
    setMessage("");
    if (auth.connected) {
      setPicker({ preCheck: false });
      return;
    }
    if (!armed) {
      setArmed(true);
      disarm.current = setTimeout(() => {
        disarm.current = null;
        setArmed(false);
      }, DRIVE_ARM_MS);
      return;
    }
    if (disarm.current !== null) clearTimeout(disarm.current);
    disarm.current = null;
    setArmed(false);
    setBusy(true);
    try {
      const next = await connect.call(client);
      setAuth(next);
      if (next.connected) setAsk(true);
    } catch (reason) {
      setMessage(localizedErrorMessage(asQcmError(reason).payload, t));
    } finally {
      setBusy(false);
    }
  };

  const label = auth.connected
    ? t("Main_BackingUpToDrive")
    : armed
      ? t("Main_PressAgainToSignIn")
      : t("Main_SignInToBackUp");
  const help = auth.connected
    ? t("Main_BackingUpToGoogleDrive")
    : armed
      ? t("Main_PressAgainToOpenGoogle")
      : t("Main_ClickToSignInAnd");

  return (
    <>
      <button
        className={auth.connected ? "home-drive connected" : "home-drive"}
        type="button"
        disabled={busy}
        aria-label={help}
        title={help}
        onClick={() => void press()}
      >
        <span aria-hidden="true">●</span> <span>{label}</span>
      </button>
      {message.length === 0 ? null : <p role="status">{message}</p>}
      <RestoreAfterConnect
        open={ask}
        onAnswer={(yes) => {
          setAsk(false);
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
    </>
  );
}
