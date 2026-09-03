/**
 * The one boundary the UI talks to the native side through.
 *
 * Components never import `@tauri-apps/*`. Device work follows the same rule as
 * profile work: opaque ids in, DTOs out, no path-shaped escape hatch.
 */

import type { CommunityCatalog } from "./communityContracts";
import type {
  AppSettings,
  AppSnapshot,
  CloseDisposition,
  CloseOutcome,
  DeletePlan,
  DeleteReceipt,
  DeviceInvalidation,
  DeviceLibrarySnapshot,
  DevicePresenceSnapshot,
  EditorOp,
  EditorSnapshot,
  InstallPlan,
  InstallProgress,
  InstallReceipt,
  LiveSnapshot,
  QcmErrorPayload,
  SaveReceipt,
  SettingsPatch,
  Subscription,
} from "./contracts";
import type { RenameDeviceProfileReceipt } from "./deviceRenameContracts";
import type { PreferenceCatalog } from "./preferenceContracts";
import type { WorkbookExportReceipt, WorkbookImportReview } from "./workbookContracts";

export interface QcmClient {
  getAppSnapshot(): Promise<AppSnapshot>;
  getSettings(): Promise<AppSettings>;
  updateSettings(expectedRevision: number, patch: SettingsPatch): Promise<AppSettings>;
  newProfile(name: string): Promise<EditorSnapshot>;
  chooseAndOpenProfile(): Promise<EditorSnapshot | null>;
  getProfileSnapshot(sessionId: string): Promise<EditorSnapshot>;
  applyEditorOps(
    sessionId: string,
    expectedRevision: number,
    ops: readonly EditorOp[],
  ): Promise<EditorSnapshot>;
  undoEditor(sessionId: string, expectedRevision: number): Promise<EditorSnapshot>;
  saveProfile(sessionId: string, expectedRevision: number): Promise<SaveReceipt>;
  saveProfileAs(sessionId: string, expectedRevision: number): Promise<SaveReceipt | null>;
  closeProfile(sessionId: string, disposition: CloseDisposition): Promise<CloseOutcome>;

  chooseAndImportWorkbook?(): Promise<WorkbookImportReview | null>;
  repairWorkbookTab?(importId: string, tabIndex: number): Promise<WorkbookImportReview>;
  acceptWorkbookImport?(importId: string): Promise<EditorSnapshot>;
  cancelWorkbookImport?(importId: string): Promise<void>;
  exportProfileXlsx?(
    sessionId: string,
    expectedRevision: number,
  ): Promise<WorkbookExportReceipt | null>;

  /** Native-owned audited metadata for prefs.csv controls. Browser fakes may omit it. */
  getPreferenceCatalog?(): Promise<PreferenceCatalog>;

  /** Community network/cache/workbook bytes stay native; the UI gets bounded rows/review only. */
  loadCommunityCatalog?(refresh: boolean): Promise<CommunityCatalog>;
  importCommunityProfile?(sheetId: string, csvName: string): Promise<WorkbookImportReview>;
  openCommunitySheet?(sheetId: string): Promise<void>;

  listDevices(): Promise<DevicePresenceSnapshot>;
  refreshDevices(): Promise<DevicePresenceSnapshot>;
  chooseDeviceFolder(): Promise<DevicePresenceSnapshot | null>;
  getDeviceLibrary(deviceId: string): Promise<DeviceLibrarySnapshot>;

  prepareInstall(sessionId: string, deviceId: string): Promise<InstallPlan>;
  commitInstall(
    planId: string,
    confirmationId?: string,
    onProgress?: (progress: InstallProgress) => void,
  ): Promise<InstallReceipt>;

  prepareDeleteDeviceProfile(
    deviceId: string,
    expectedGeneration: number,
    name: string,
  ): Promise<DeletePlan>;
  commitDeleteDeviceProfile(planId: string, confirmationId: string): Promise<DeleteReceipt>;
  renameDeviceProfile?(
    deviceId: string,
    expectedGeneration: number,
    from: string,
    to: string,
  ): Promise<RenameDeviceProfileReceipt>;

  openDeviceProfile(
    deviceId: string,
    expectedGeneration: number,
    name: string,
  ): Promise<EditorSnapshot>;
  openDevicePreferences(deviceId: string, expectedGeneration: number): Promise<EditorSnapshot>;

  startLiveInput(onFrame: (frame: LiveSnapshot) => void): Promise<Subscription>;
  subscribeDevicesChanged(onChanged: (event: DeviceInvalidation) => void): Promise<Subscription>;
}

export class QcmCommandError extends Error {
  readonly payload: QcmErrorPayload;

  constructor(payload: QcmErrorPayload) {
    super(payload.message);
    this.name = "QcmCommandError";
    this.payload = payload;
  }

  get code(): string {
    return this.payload.code;
  }

  get recoverable(): boolean {
    return this.payload.recoverable;
  }
}

export function isQcmCommandError(value: unknown): value is QcmCommandError {
  return value instanceof QcmCommandError;
}

export function asQcmError(reason: unknown): QcmCommandError {
  if (isQcmCommandError(reason)) return reason;
  if (isErrorPayload(reason)) return new QcmCommandError(reason);
  return new QcmCommandError({
    code: "QCM_INTERNAL",
    message: "Something went wrong inside the app.",
    recoverable: false,
    action: { kind: "report_bug" },
    operationId: null,
    targetState: null,
    backup: null,
  });
}

function isErrorPayload(value: unknown): value is QcmErrorPayload {
  if (typeof value !== "object" || value === null) return false;
  const candidate = value as Partial<QcmErrorPayload>;
  return typeof candidate.code === "string" && typeof candidate.message === "string";
}
