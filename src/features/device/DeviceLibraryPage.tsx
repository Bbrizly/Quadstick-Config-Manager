import { useCallback, useEffect, useMemo, useState } from "react";

import { Dialog } from "../../components/primitives/Dialog";
import { useI18n } from "../../i18n";
import { asQcmError, type DeletePlan, type DeviceLibrarySnapshot, type DevicePresenceSnapshot, type EditorSnapshot, type QcmClient } from "../../platform";

interface DeviceLibraryPageProps {
  readonly client: QcmClient;
  readonly onOpenProfile: (snapshot: EditorSnapshot) => void;
}

export function DeviceLibraryPage({ client, onOpenProfile }: DeviceLibraryPageProps) {
  const { plural, t } = useI18n();
  const [presence, setPresence] = useState<DevicePresenceSnapshot | null>(null);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [library, setLibrary] = useState<DeviceLibrarySnapshot | null>(null);
  const [deletePlan, setDeletePlan] = useState<DeletePlan | null>(null);
  const [deleteDeviceId, setDeleteDeviceId] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");

  const selected = useMemo(
    () => presence?.devices.find((device) => device.deviceId === selectedId) ?? null,
    [presence, selectedId],
  );

  const showFailure = useCallback((reason: unknown): void => {
    setMessage(asQcmError(reason).payload.message);
  }, []);

  const applyPresence = useCallback((next: DevicePresenceSnapshot): void => {
    setPresence(next);
    setSelectedId((current) => {
      if (current !== null && next.devices.some((device) => device.deviceId === current)) {
        return current;
      }
      return next.devices[0]?.deviceId ?? null;
    });
  }, []);

  const refresh = useCallback(async (): Promise<void> => {
    setBusy(true);
    try {
      applyPresence(await client.refreshDevices());
      setMessage("");
    } catch (reason) {
      showFailure(reason);
    } finally {
      setBusy(false);
    }
  }, [applyPresence, client, showFailure]);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  useEffect(() => {
    let active = true;
    let subscription: { dispose(): void } | null = null;
    void client.subscribeDevicesChanged(() => {
      if (active) void refresh();
    }).then((value) => {
      if (active) subscription = value;
      else value.dispose();
    }).catch(showFailure);
    return () => {
      active = false;
      subscription?.dispose();
    };
  }, [client, refresh, showFailure]);

  useEffect(() => {
    if (selectedId === null) {
      setLibrary(null);
      return;
    }
    let active = true;
    setLibrary(null);
    void client.getDeviceLibrary(selectedId).then((next) => {
      if (active) setLibrary(next);
    }).catch((reason) => {
      if (active) showFailure(reason);
    });
    return () => {
      active = false;
    };
  }, [client, selectedId, showFailure]);

  const chooseDevice = async (): Promise<void> => {
    if (busy) return;
    setBusy(true);
    try {
      const next = await client.chooseDeviceFolder();
      if (next !== null) applyPresence(next);
      setMessage("");
    } catch (reason) {
      showFailure(reason);
    } finally {
      setBusy(false);
    }
  };

  const openProfile = async (name: string): Promise<void> => {
    if (selected === null || library === null || busy) return;
    setBusy(true);
    try {
      onOpenProfile(await client.openDeviceProfile(selected.deviceId, library.generation, name));
    } catch (reason) {
      showFailure(reason);
    } finally {
      setBusy(false);
    }
  };

  const openPreferences = async (): Promise<void> => {
    if (selected === null || library === null || busy) return;
    setBusy(true);
    try {
      onOpenProfile(await client.openDevicePreferences(selected.deviceId, library.generation));
    } catch (reason) {
      showFailure(reason);
    } finally {
      setBusy(false);
    }
  };

  const prepareDelete = async (name: string): Promise<void> => {
    if (selected === null || library === null || busy) return;
    setBusy(true);
    try {
      const plan = await client.prepareDeleteDeviceProfile(
        selected.deviceId,
        library.generation,
        name,
      );
      setDeletePlan(plan);
      setDeleteDeviceId(selected.deviceId);
    } catch (reason) {
      showFailure(reason);
    } finally {
      setBusy(false);
    }
  };

  const commitDelete = async (): Promise<void> => {
    if (deletePlan === null || deleteDeviceId === null || busy) return;
    setBusy(true);
    try {
      const receipt = await client.commitDeleteDeviceProfile(
        deletePlan.planId,
        deletePlan.confirmation.confirmationId,
      );
      setDeletePlan(null);
      setDeleteDeviceId(null);
      setMessage(t("Device_DeletedResultDeletedPathACopy", [receipt.name, receipt.backup]));
      if (selectedId !== null) setLibrary(await client.getDeviceLibrary(selectedId));
    } catch (reason) {
      showFailure(reason);
    } finally {
      setBusy(false);
    }
  };

  const devices = presence?.devices ?? [];

  return (
    <section className="device-page" aria-labelledby="device-page-title">
      <header className="feature-page-header">
        <div>
          <h1 id="device-page-title">{t("Shell_OnYourQuadStick")}</h1>
          <p>{t("Device_EverythingHereReadsAndWrites")}</p>
        </div>
        <div className="feature-page-actions">
          <button type="button" disabled={busy} onClick={() => void refresh()}>
            {t("Device_Refresh")}
          </button>
          <button type="button" disabled={busy} onClick={() => void chooseDevice()}>
            {t("Install_ChooseTheQuadStickDrive")}
          </button>
        </div>
      </header>

      {message === "" ? null : <p role="status" className="feature-status">{message}</p>}

      {presence === null ? (
        <p role="status">{t("Device_LookingForYourQuadStick")}</p>
      ) : devices.length === 0 ? (
        <div className="empty-state">
          <p>{t("Device_NoQuadStickDriveIsPlugged")}</p>
          <p>{t("Shell_PlugInYourQuadStickTo")}</p>
        </div>
      ) : (
        <>
          {devices.length > 1 ? (
            <label className="device-picker-label">
              <span>{t("Install_ChooseTheQuadStickDrive")}</span>
              <select
                value={selectedId ?? ""}
                onChange={(event) => setSelectedId(event.currentTarget.value)}
              >
                {devices.map((device) => (
                  <option key={device.deviceId} value={device.deviceId}>{device.displayName}</option>
                ))}
              </select>
            </label>
          ) : null}

          {selected === null ? null : (
            <section className="device-card" aria-labelledby="selected-device-name">
              <div className="device-card-heading">
                <div>
                  <h2 id="selected-device-name">{selected.displayName}</h2>
                  <p>{selected.writable ? t("Main_NoProblemsReadyToSave") : t("DevicePage_PlugInYourQuadStickTo")}</p>
                </div>
                <button type="button" disabled={busy || library === null} onClick={() => void openPreferences()}>
                  {t("Main_DeviceSettings")}
                </button>
              </div>

              {library === null ? (
                <p role="status">{t("Device_LookingForYourQuadStick")}</p>
              ) : library.files.length === 0 ? (
                <p>{t("Device_ThisDriveHasNoCsv")}</p>
              ) : (
                <>
                  <ul className="device-file-list">
                    {library.files.map((file) => (
                      <li key={file.name} className="device-file-row">
                        <div className="device-file-copy">
                          <strong>{t("Device_EntryNumberEntryFileName", [file.fileNumber, file.name])}</strong>
                          <span>
                            {plural("Count_File", 1, [1])}
                            {file.protected ? t("Device_ProtectedItCannotBeDeleted") : ""}
                          </span>
                        </div>
                        <div className="device-file-actions">
                          <button
                            type="button"
                            disabled={busy}
                            aria-label={t("Device_OpenFileNameFromGroup", [file.name, selected.displayName])}
                            onClick={() => void openProfile(file.name)}
                          >
                            {t("Shell_Open")}
                          </button>
                          <button
                            type="button"
                            disabled={busy || file.protected}
                            aria-label={t("Device_DeleteFileNameFromThe", [file.name, selected.displayName])}
                            onClick={() => void prepareDelete(file.name)}
                          >
                            {t("Main_Delete")}
                          </button>
                        </div>
                      </li>
                    ))}
                  </ul>

                  <section className="device-guide" aria-labelledby="device-guide-title">
                    <h3 id="device-guide-title">{t("Device_FileSelectionOrderAndLights")}</h3>
                    <p>{t("Device_PushingTheProfileSwitchSteps")}</p>
                    <ol>
                      {library.files.map((file) => (
                        <li key={`guide-${file.name}`}>
                          <strong>{file.name}</strong>
                          {file.lights.length === 0
                            ? ` — ${t("Device_NoLightPatternIsDocumented")}`
                            : ` — ${file.lights.join(", ")}`}
                        </li>
                      ))}
                    </ol>
                  </section>
                </>
              )}
            </section>
          )}
        </>
      )}

      <Dialog
        open={deletePlan !== null}
        title={deletePlan === null ? t("Main_Delete") : t("Device_DeleteFileFromGroup", [deletePlan.name, selected?.displayName ?? ""])}
        onClose={() => {
          if (!busy) {
            setDeletePlan(null);
            setDeleteDeviceId(null);
          }
        }}
        actions={
          <>
            <button type="button" disabled={busy} onClick={() => {
              setDeletePlan(null);
              setDeleteDeviceId(null);
            }}>
              {t("Device_Cancel")}
            </button>
            <button className="primary-action" type="button" data-autofocus disabled={busy} onClick={() => void commitDelete()}>
              {t("Main_Delete")}
            </button>
          </>
        }
      >
        <p>{deletePlan?.confirmation.summary}</p>
      </Dialog>
    </section>
  );
}
