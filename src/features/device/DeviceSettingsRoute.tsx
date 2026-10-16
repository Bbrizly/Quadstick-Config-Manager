import { useCallback, useEffect, useRef, useState } from "react";

import { Dialog } from "../../components/primitives/Dialog";
import { useI18n } from "../../i18n";
import { localizedErrorMessage } from "../../i18n/errors";
import { asQcmError, type DeviceSummary, type EditorSnapshot, type QcmClient } from "../../platform";
import { DevicePreferencesPage } from "./DevicePreferencesPage";

interface DeviceSettingsRouteProps {
  readonly client: QcmClient;
  /** The open prefs.csv session, kept by the app across a trip elsewhere. */
  readonly snapshot: EditorSnapshot | null;
  readonly onSnapshot: (snapshot: EditorSnapshot | null) => void;
  readonly onManageFiles: () => void;
}

/**
 * The device nav (`ShowDevicePageAsync` / `LoadDeviceSettingsAsync`): find
 * the drive, read prefs.csv off it, draw the settings. One drive opens at
 * once, two or more ask which (`PickDeviceRootAsync`), none draws the
 * catalog with a line saying so. The file list is a window off this page.
 */
export function DeviceSettingsRoute({ client, snapshot, onSnapshot, onManageFiles }: DeviceSettingsRouteProps) {
  const { t } = useI18n();
  const [status, setStatus] = useState(snapshot === null ? t("DevicePage_LookingForYourQuadStick") : "");
  const [choices, setChoices] = useState<readonly DeviceSummary[] | null>(null);
  const [pick, setPick] = useState("");
  const snapshotRef = useRef(snapshot);
  useEffect(() => {
    snapshotRef.current = snapshot;
  }, [snapshot]);

  const open = useCallback(
    async (device: DeviceSummary): Promise<void> => {
      try {
        onSnapshot(await client.openDevicePreferences(device.deviceId, device.generation));
        setStatus("");
      } catch (reason) {
        onSnapshot(null);
        const error = asQcmError(reason).payload;
        setStatus(
          error.code === "QCM_STORAGE_FILE_NOT_FOUND"
            ? `${t("DevicePage_FoundYourQuadStickAtRoot", [device.displayName])} ${t("DevicePage_ItHasNoSettingsFileYet")}`
            : t("DevicePage_CouldNotReadTheSettings", [localizedErrorMessage(error, t)]),
        );
      }
    },
    [client, onSnapshot, t],
  );

  const load = useCallback(async (): Promise<void> => {
    const current = snapshotRef.current;
    if (current !== null) {
      await client.closeProfile(current.sessionId, "discard").catch(() => undefined);
      onSnapshot(null);
    }
    setStatus(t("DevicePage_LookingForYourQuadStick"));
    try {
      const presence = await client.refreshDevices();
      const devices = presence.devices;
      if (devices.length === 0) {
        setStatus(t("DevicePage_NoQuadStickIsPluggedIn"));
      } else if (devices.length === 1) {
        await open(devices[0]!);
      } else {
        setPick(devices[0]!.deviceId);
        setChoices(devices);
      }
    } catch (reason) {
      setStatus(t("DevicePage_CouldNotReadTheSettings", [localizedErrorMessage(asQcmError(reason).payload, t)]));
    }
  }, [client, onSnapshot, open, t]);

  // Read on arrival only: an open session survives a trip to another page.
  const started = useRef(false);
  useEffect(() => {
    if (started.current) return;
    started.current = true;
    if (snapshotRef.current === null) void load();
  }, [load]);

  const chooseDrive = async (): Promise<void> => {
    try {
      const next = await client.chooseDeviceFolder();
      if (next !== null) await load();
    } catch (reason) {
      setStatus(localizedErrorMessage(asQcmError(reason).payload, t));
    }
  };

  return (
    <>
      <DevicePreferencesPage
        client={client}
        snapshot={snapshot}
        status={status}
        onSnapshot={onSnapshot}
        onReload={() => void load()}
        onManageFiles={onManageFiles}
        onChooseDrive={() => void chooseDrive()}
      />
      <Dialog
        open={choices !== null}
        title={t("Install_ChooseTheQuadStickDrive")}
        onClose={() => {
          setChoices(null);
          setStatus(t("DevicePage_NoQuadStickIsPluggedIn"));
        }}
        actions={
          <>
            <button
              className="primary"
              type="button"
              onClick={() => {
                const device = choices?.find((candidate) => candidate.deviceId === pick);
                setChoices(null);
                if (device !== undefined) void open(device);
              }}
            >
              {t("Shell_Open")}
            </button>
            <button
              type="button"
              onClick={() => {
                setChoices(null);
                setStatus(t("DevicePage_NoQuadStickIsPluggedIn"));
              }}
            >
              {t("Device_Cancel")}
            </button>
          </>
        }
      >
        <div className="template-list" role="radiogroup" aria-label={t("Install_ChooseTheQuadStickDrive")}>
          {(choices ?? []).map((device) => (
            <label className="template-option" key={device.deviceId}>
              <input
                type="radio"
                name="device-root"
                checked={pick === device.deviceId}
                onChange={() => setPick(device.deviceId)}
              />
              <span>{device.displayName}</span>
            </label>
          ))}
        </div>
      </Dialog>
    </>
  );
}
