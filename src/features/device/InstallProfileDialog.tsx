import { useEffect, useState } from "react";

import { Dialog } from "../../components/primitives/Dialog";
import { useI18n } from "../../i18n";
import { localizedErrorMessage } from "../../i18n/errors";
import {
  asQcmError,
  type DevicePresenceSnapshot,
  type EditorSnapshot,
  type InstallPlan,
  type InstallReceipt,
  type QcmClient,
} from "../../platform";

interface InstallProfileDialogProps {
  readonly client: QcmClient;
  readonly profile: EditorSnapshot;
  readonly open: boolean;
  readonly onClose: () => void;
}

export function InstallProfileDialog({ client, profile, open, onClose }: InstallProfileDialogProps) {
  const { t } = useI18n();
  const [presence, setPresence] = useState<DevicePresenceSnapshot | null>(null);
  const [deviceId, setDeviceId] = useState<string>("");
  const [plan, setPlan] = useState<InstallPlan | null>(null);
  const [receipt, setReceipt] = useState<InstallReceipt | null>(null);
  const [stages, setStages] = useState<readonly string[]>([]);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");

  useEffect(() => {
    if (!open) return undefined;
    let active = true;
    void client
      .refreshDevices()
      .then((next) => {
        if (!active) return;
        setPresence(next);
        setDeviceId((current) =>
          current !== "" && next.devices.some((device) => device.deviceId === current)
            ? current
            : next.devices[0]?.deviceId ?? "",
        );
        setPlan(null);
        setReceipt(null);
        setStages([]);
        setMessage("");
      })
      .catch((reason: unknown) => {
        if (active) setMessage(localizedErrorMessage(asQcmError(reason).payload, t));
      });
    return () => {
      active = false;
    };
  }, [client, open, t]);

  const chooseDevice = async (): Promise<void> => {
    if (busy) return;
    setBusy(true);
    try {
      const next = await client.chooseDeviceFolder();
      if (next !== null) {
        setPresence(next);
        setDeviceId(next.devices[0]?.deviceId ?? "");
        setPlan(null);
      }
    } catch (reason) {
      setMessage(localizedErrorMessage(asQcmError(reason).payload, t));
    } finally {
      setBusy(false);
    }
  };

  const installPlan = async (chosen: InstallPlan): Promise<void> => {
    const installed = await client.commitInstall(
      chosen.planId,
      chosen.confirmation?.confirmationId,
      (progress) => setStages((previous) => [...previous, progress.stage]),
    );
    setReceipt(installed);
    setPlan(null);
    setMessage(
      t("Install_InstalledPathGetFileNameResultInstalledPath", [
        installed.target,
        presence?.devices.find((device) => device.deviceId === installed.deviceId)?.displayName ??
          installed.deviceId,
      ]),
    );
  };

  const prepare = async (): Promise<void> => {
    if (busy || deviceId === "" || profile.errorCount > 0) return;
    setBusy(true);
    try {
      setReceipt(null);
      setStages([]);
      setMessage("");
      const next = await client.prepareInstall(profile.sessionId, deviceId);
      if (next.confirmation === null) {
        await installPlan(next);
      } else {
        setPlan(next);
      }
    } catch (reason) {
      setMessage(localizedErrorMessage(asQcmError(reason).payload, t));
    } finally {
      setBusy(false);
    }
  };

  const confirmInstall = async (): Promise<void> => {
    if (plan === null || busy) return;
    setBusy(true);
    try {
      await installPlan(plan);
    } catch (reason) {
      setMessage(localizedErrorMessage(asQcmError(reason).payload, t));
    } finally {
      setBusy(false);
    }
  };

  const devices = presence?.devices ?? [];
  const confirmation = plan?.confirmation ?? null;

  return (
    <Dialog
      open={open}
      title={t("Install_InstallingProfile")}
      onClose={() => {
        if (!busy) onClose();
      }}
      actions={
        receipt === null ? (
          <>
            <button type="button" disabled={busy} onClick={onClose}>
              {t("Device_Cancel")}
            </button>
            {confirmation === null ? (
              <button
                className="primary-action"
                type="button"
                data-autofocus
                disabled={busy || deviceId === "" || profile.errorCount > 0}
                onClick={() => void prepare()}
              >
                {t("Shell_InstallToQuadStick")}
              </button>
            ) : (
              <button
                className="primary-action"
                type="button"
                data-autofocus
                disabled={busy}
                onClick={() => void confirmInstall()}
              >
                {t("Main_YesContinue")}
              </button>
            )}
          </>
        ) : (
          <button className="primary-action" type="button" data-autofocus onClick={onClose}>
            {t("Install_Close")}
          </button>
        )
      }
    >
      <div className="install-dialog-body">
        {profile.errorCount > 0 ? (
          <p className="feature-error">{t("Install_FixTheErrorsInThe")}</p>
        ) : null}

        {devices.length === 0 ? (
          <div className="empty-state">
            <p>{t("Install_NoQuadStickDriveFoundA")}</p>
            <button type="button" disabled={busy} onClick={() => void chooseDevice()}>
              {t("Install_ChooseTheQuadStickDrive")}
            </button>
          </div>
        ) : (
          <label className="device-picker-label">
            <span>{t("Install_InstallingTo")}</span>
            <select
              value={deviceId}
              disabled={busy || confirmation !== null}
              onChange={(event) => {
                setDeviceId(event.currentTarget.value);
                setPlan(null);
                setReceipt(null);
                setStages([]);
              }}
            >
              {devices.map((device) => (
                <option key={device.deviceId} value={device.deviceId}>
                  {device.displayName}
                </option>
              ))}
            </select>
          </label>
        )}

        {confirmation === null ? null : (
          <section className="confirmation-copy" aria-live="assertive">
            <h3>
              {confirmation.kind === "overwrite_default_csv"
                ? t("Install_OverwriteDefaultCsv")
                : confirmation.kind === "overwrite_device_preferences"
                  ? t("Install_InstallPrefsCsvToThis")
                  : t("Main_SaveYourChanges")}
            </h3>
            <p>{confirmation.summary}</p>
            {confirmation.kind === "overwrite_default_csv" ? (
              <p>{t("Install_AWrongDefaultCsvCan")}</p>
            ) : null}
          </section>
        )}

        {busy && stages.length === 0 ? (
          <output className="feature-status">{t("Install_BackingUpAndInstalling")}</output>
        ) : null}
        {stages.length > 0 ? (
          <ol className="install-stage-list" aria-live="polite">
            {stages.map((stage) => (
              <li key={stage}>{stage}</li>
            ))}
          </ol>
        ) : null}

        {receipt?.backup === null || receipt?.backup === undefined ? null : (
          <p>{t("Install_BackupPath", [receipt.backup])}</p>
        )}
        {message === "" ? null : <output className="feature-status">{message}</output>}
      </div>
    </Dialog>
  );
}
