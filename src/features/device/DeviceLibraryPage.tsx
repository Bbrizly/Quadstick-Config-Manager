import { useCallback, useEffect, useMemo, useState } from "react";

import { Dialog } from "../../components/primitives/Dialog";
import { useI18n } from "../../i18n";
import { localizedErrorMessage } from "../../i18n/errors";
import { InfoButton } from "../editor/Popover";
import { ControllerRow } from "./ControllerRow";
import { DeviceLinkDialog } from "./DeviceLinkDialog";
import {
  asQcmError,
  type DeletePlan,
  type DeviceLibrarySnapshot,
  type DevicePresenceSnapshot,
  type EditorSnapshot,
  type QcmClient,
} from "../../platform";

interface DeviceLibraryPageProps {
  readonly client: QcmClient;
  readonly onOpenProfile: (snapshot: EditorSnapshot) => void;
  /** Inside the files window: the page heading, settings and link live on
   * the settings page, which is where Avalonia's window opens from. */
  readonly inDialog?: boolean;
  /** The files window's Close, drawn beside Refresh as Avalonia does. */
  readonly onClose?: () => void;
}

/** `Validator.ModesWithNoDrive`: the USB emulation modes that leave no drive
 * for a computer to find. The "no drive" sentence names them. */
const MODES_WITH_NO_DRIVE = [1, 3, 5, 6, 7] as const;

function csvName(value: string): string {
  const trimmed = value.trim();
  if (trimmed.toLowerCase().endsWith(".csv")) return trimmed;
  return `${trimmed}.csv`;
}

export function DeviceLibraryPage({ client, onOpenProfile, inDialog = false, onClose }: DeviceLibraryPageProps) {
  const { t, plural } = useI18n();
  const [presence, setPresence] = useState<DevicePresenceSnapshot | null>(null);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [library, setLibrary] = useState<DeviceLibrarySnapshot | null>(null);
  const [deletePlan, setDeletePlan] = useState<DeletePlan | null>(null);
  const [deleteDeviceId, setDeleteDeviceId] = useState<string | null>(null);
  const [renameFrom, setRenameFrom] = useState<string | null>(null);
  const [linkOpen, setLinkOpen] = useState(false);
  const closeLink = useCallback(() => setLinkOpen(false), []);
  const [renameTo, setRenameTo] = useState("");
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  // `MacDriveAccess.Needed`: only the sandboxed Mac build has to be handed
  // the drive. A normal build finds it on its own.
  const [driveAccessNeeded, setDriveAccessNeeded] = useState(false);
  useEffect(() => {
    let active = true;
    void client.controller
      ?.getControllerOptions()
      .then((options) => {
        if (active) setDriveAccessNeeded(options.kind === "mac" && options.macSandboxed);
      })
      .catch(() => undefined);
    return () => {
      active = false;
    };
  }, [client]);

  const selected = useMemo(
    () => presence?.devices.find((device) => device.deviceId === selectedId) ?? null,
    [presence, selectedId],
  );
  const selectedLibrary = library !== null && library.deviceId === selectedId ? library : null;

  const showFailure = useCallback((reason: unknown): void => {
    setMessage(localizedErrorMessage(asQcmError(reason).payload, t));
  }, [t]);

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
    let active = true;
    void client
      .refreshDevices()
      .then((next) => {
        if (!active) return;
        applyPresence(next);
        setMessage("");
      })
      .catch((reason: unknown) => {
        if (active) showFailure(reason);
      });
    return () => {
      active = false;
    };
  }, [applyPresence, client, showFailure]);

  useEffect(() => {
    let active = true;
    let subscription: { dispose(): void } | null = null;
    void client
      .subscribeDevicesChanged(() => {
        if (active) void refresh();
      })
      .then((value) => {
        if (active) subscription = value;
        else value.dispose();
      })
      .catch(showFailure);
    return () => {
      active = false;
      subscription?.dispose();
    };
  }, [client, refresh, showFailure]);

  useEffect(() => {
    if (selectedId === null) return undefined;
    const requestedId = selectedId;
    let active = true;
    void client
      .getDeviceLibrary(requestedId)
      .then((next) => {
        if (active) setLibrary(next);
      })
      .catch((reason: unknown) => {
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
    if (selected === null || selectedLibrary === null || busy) return;
    setBusy(true);
    try {
      onOpenProfile(
        await client.openDeviceProfile(selected.deviceId, selectedLibrary.generation, name),
      );
    } catch (reason) {
      showFailure(reason);
    } finally {
      setBusy(false);
    }
  };

  const openPreferences = async (): Promise<void> => {
    if (selected === null || selectedLibrary === null || busy) return;
    setBusy(true);
    try {
      onOpenProfile(
        await client.openDevicePreferences(selected.deviceId, selectedLibrary.generation),
      );
    } catch (reason) {
      showFailure(reason);
    } finally {
      setBusy(false);
    }
  };

  const prepareDelete = async (name: string): Promise<void> => {
    if (selected === null || selectedLibrary === null || busy) return;
    setBusy(true);
    try {
      const prepared = await client.prepareDeleteDeviceProfile(
        selected.deviceId,
        selectedLibrary.generation,
        name,
      );
      setDeletePlan(prepared);
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

  const startRename = (name: string): void => {
    setRenameFrom(name);
    setRenameTo(name.replace(/\.csv$/iu, ""));
  };

  const commitRename = async (): Promise<void> => {
    const rename = client.renameDeviceProfile;
    if (
      rename === undefined ||
      renameFrom === null ||
      selected === null ||
      selectedLibrary === null ||
      busy
    ) {
      return;
    }
    setBusy(true);
    try {
      const receipt = await rename.call(
        client,
        selected.deviceId,
        selectedLibrary.generation,
        renameFrom,
        csvName(renameTo),
      );
      setRenameFrom(null);
      setMessage(t("Names_RenamedOldToNew", [receipt.from, receipt.to]));
      setLibrary(await client.getDeviceLibrary(selected.deviceId));
    } catch (reason) {
      showFailure(reason);
    } finally {
      setBusy(false);
    }
  };

  const devices = presence?.devices ?? [];
  const noDrive = t("Device_NoQuadStickDriveIsPlugged", [MODES_WITH_NO_DRIVE.join(", ")]);
  const refreshButton = (
    <button type="button" disabled={busy} onClick={() => void refresh()}>
      {t("Device_Refresh")}
    </button>
  );
  const chooseButton = !driveAccessNeeded ? null : (
    <button type="button" disabled={busy} onClick={() => void chooseDevice()}>
      {t("Install_ChooseTheQuadStickDrive")}
    </button>
  );
  // `DeviceFilesWindow._summary`: the one muted line under the explanation.
  const dialogSummary =
    presence === null
      ? t("Device_LookingForYourQuadStick")
      : devices.length === 0
        ? noDrive
        : selected !== null && selectedLibrary !== null && devices.length === 1
          ? t("Device_FilesTotalOnGroup", [
              plural("Count_File", selectedLibrary.files.length, [selectedLibrary.files.length]),
              selected.displayName,
            ])
          : "";

  return (
    <section
      className={inDialog ? "device-page in-dialog" : "device-page"}
      {...(inDialog ? { "aria-label": t("Device_FilesOnYourQuadStick") } : { "aria-labelledby": "device-page-title" })}
    >
      {inDialog ? (
        // `DeviceFilesWindow`: the explanation with its "?" at the right, the
        // muted summary, the drives, then Refresh and Close at the foot.
        <>
          <div className="explain-row device-files-explain">
            <span>{t("Device_ManageFilesOnTheQuadStick")}</span>
            <InfoButton
              name={t("Rewrite_HowDoesManagingFilesWork")}
              title={t("Device_FilesOnYourQuadStick")}
              body={t("Device_EverythingHereReadsAndWrites")}
            />
          </div>
          {dialogSummary === "" ? null : <p className="device-files-summary">{dialogSummary}</p>}
        </>
      ) : (
        <>
          <header className="feature-page-header">
            <div>
              <h1 id="device-page-title">{t("Shell_OnYourQuadStick")}</h1>
              <p>{t("Device_EverythingHereReadsAndWrites")}</p>
            </div>
            <div className="feature-page-actions">
              {refreshButton}
              {chooseButton}
              {client.console === undefined ? null : (
                <button
                  type="button"
                  aria-label={t("Link_PageButtonName")}
                  onClick={() => setLinkOpen(true)}
                >
                  {t("Link_PageButton")}
                </button>
              )}
            </div>
          </header>
          {client.controller === undefined ? null : <ControllerRow controller={client.controller} />}
          {message === "" ? null : <output className="feature-status">{message}</output>}
        </>
      )}

      <div className={inDialog ? "device-files-groups" : "device-page-body"}>
        {presence === null ? (
          inDialog ? null : <output className="feature-status">{t("Device_LookingForYourQuadStick")}</output>
        ) : devices.length === 0 ? (
          inDialog ? (
            // Where Avalonia's `DriveAccessButton` sits: under the summary, and
            // only while no drive is found.
            chooseButton === null ? null : <div className="device-files-choose">{chooseButton}</div>
          ) : (
            <div className="empty-state">
              <p>{noDrive}</p>
              <p>{t("Shell_PlugInYourQuadStickTo")}</p>
            </div>
          )
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
                    <option key={device.deviceId} value={device.deviceId}>
                      {device.displayName}
                    </option>
                  ))}
                </select>
              </label>
            ) : null}

            {selected === null ? null : (
              <section className="device-card" aria-labelledby="selected-device-name">
                <div className="device-card-heading">
                  <div>
                    <h2 id="selected-device-name">{selected.displayName}</h2>
                    <p>
                      {selected.writable
                        ? t("Main_NoProblemsReadyToSave")
                        : t("DevicePage_PlugInYourQuadStickTo")}
                    </p>
                  </div>
                  {inDialog ? null : (
                    <button
                      type="button"
                      disabled={busy || selectedLibrary === null}
                      onClick={() => void openPreferences()}
                    >
                      {t("Main_DeviceSettings")}
                    </button>
                  )}
                </div>

                {selectedLibrary === null ? (
                  <output className="feature-status">{t("Device_LookingForYourQuadStick")}</output>
                ) : selectedLibrary.files.length === 0 ? (
                  <p>{t("Device_ThisDriveHasNoCsv")}</p>
                ) : (
                  <>
                    <ul className="device-file-list">
                      {selectedLibrary.files.map((file, index) => (
                        <li key={file.name} className="device-file-row">
                          <div className="device-file-copy">
                            <strong id={`device-file-${String(index)}`}>
                              {t("Device_EntryNumberEntryFileName", [file.fileNumber, file.name])}
                            </strong>
                            {file.protected ? (
                              <span>{t("Device_ProtectedItCannotBeDeleted")}</span>
                            ) : null}
                          </div>
                          <div className="device-file-actions">
                            <button
                              type="button"
                              disabled={busy}
                              aria-label={t("Device_OpenFileNameFromGroup", [
                                file.name,
                                selected.displayName,
                              ])}
                              onClick={() => void openProfile(file.name)}
                            >
                              {t("Shell_Open")}
                            </button>
                            {client.renameDeviceProfile === undefined ? null : (
                              <button
                                type="button"
                                id={`device-file-${String(index)}-rename`}
                                // Rename alone says nothing of which file.
                                aria-labelledby={`device-file-${String(index)}-rename device-file-${String(index)}`}
                                disabled={busy || file.protected}
                                onClick={() => startRename(file.name)}
                              >
                                {t("Main_Rename")}
                              </button>
                            )}
                            <button
                              type="button"
                              disabled={busy || file.protected}
                              aria-label={t("Device_DeleteFileNameFromThe", [
                                file.name,
                                selected.displayName,
                              ])}
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
                        {selectedLibrary.files.map((file) => (
                          <li key={`guide-${file.name}`}>
                            <strong>{file.name}</strong>
                            {file.lights.length === 0
                              ? `: ${t("Device_NoLightPatternIsDocumented")}`
                              : `: ${file.lights.join(", ")}`}
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
      </div>

      {inDialog ? (
        <footer className="device-files-foot">
          <div className="device-files-buttons">
            {refreshButton}
            <button type="button" onClick={onClose}>
              {t("Device_Close")}
            </button>
          </div>
          <output className="device-files-status">{message}</output>
        </footer>
      ) : null}

      <Dialog
        open={renameFrom !== null}
        title={t("Main_Rename")}
        onClose={() => {
          if (!busy) setRenameFrom(null);
        }}
        actions={
          <>
            <button type="button" disabled={busy} onClick={() => setRenameFrom(null)}>
              {t("Device_Cancel")}
            </button>
            <button
              className="primary-action"
              type="button"
              disabled={busy || renameTo.trim() === ""}
              onClick={() => void commitRename()}
            >
              {t("Main_Rename")}
            </button>
          </>
        }
      >
        <label className="device-rename-field">
          <span>{t("Shell_ProfileNameEditToRename")}</span>
          <input
            data-autofocus
            value={renameTo}
            disabled={busy}
            onChange={(event) => setRenameTo(event.currentTarget.value)}
            onKeyDown={(event) => {
              if (event.key === "Enter" && renameTo.trim() !== "") void commitRename();
            }}
          />
        </label>
        <p>{t("Device_PushingTheProfileSwitchSteps")}</p>
      </Dialog>

      <Dialog
        open={deletePlan !== null}
        title={
          deletePlan === null
            ? t("Main_Delete")
            : t("Device_DeleteFileFromGroup", [deletePlan.name, selected?.displayName ?? ""])
        }
        onClose={() => {
          if (!busy) {
            setDeletePlan(null);
            setDeleteDeviceId(null);
          }
        }}
        actions={
          <>
            {/* The safe answer is the default, as Home's delete and
                Avalonia's ConfirmAsync: a stray Enter keeps the file. */}
            <button
              type="button"
              data-autofocus
              disabled={busy}
              onClick={() => {
                setDeletePlan(null);
                setDeleteDeviceId(null);
              }}
            >
              {t("Device_Cancel")}
            </button>
            <button
              className="danger"
              type="button"
              disabled={busy}
              onClick={() => void commitDelete()}
            >
              {t("Main_Delete")}
            </button>
          </>
        }
      >
        <p>{deletePlan?.confirmation.summary}</p>
      </Dialog>

      {/* Mounted only while open, so every opening starts disconnected. */}
      {client.console === undefined || !linkOpen ? null : (
        <DeviceLinkDialog client={client} console={client.console} open onClose={closeLink} />
      )}
    </section>
  );
}
