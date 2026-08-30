/**
 * The one boundary the UI talks to the native side through.
 *
 * Components never import `@tauri-apps/*`. Device work follows the same rule as
 * profile work: opaque ids in, DTOs out, no path-shaped escape hatch.
 */

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

export interface QcmClient {
  getAppSnapshot(): Promise<AppSnapshot>;
  getSettings(): Promise<AppSettings>;
  updateSettings(expectedRevision: number, patch: SettingsPatch): Promise<AppSettings>;
  newProfile(name: string): Promise<EditorSnapshot>;
  chooseAndOpenProfile(): Promise<EditorSnapshot | null>;
  applyEditorOps(
    sessionId: string,
    expectedRevision: number,
    ops: readonly EditorOp[],
  ): Promise<EditorSnapshot>;
  undoEditor(sessionId: string, expectedRevision: number): Promise<EditorSnapshot>;
  saveProfile(sessionId: string, expectedRevision: number): Promise<SaveReceipt>;
  saveProfileAs(sessionId: string, expectedRevision: number): Promise<SaveReceipt | null>;
  closeProfile(sessionId: string, disposition: CloseDisposition): Promise<CloseOutcome>;

  /** Display-only discovery. Every write revalidates natively. */
  listDevices(): Promise<DevicePresenceSnapshot>;
  refreshDevices(): Promise<DevicePresenceSnapshot>;
  /** Native folder dialog. null means cancel; no selected path crosses here. */
  chooseDeviceFolder(): Promise<DevicePresenceSnapshot | null>;
  getDeviceLibrary(deviceId: string): Promise<DeviceLibrarySnapshot>;

  /**
   * Prepare from the canonical open session. The returned plan id names native
   * state; it is not authority and carries no bytes.
   */
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

  /** Device reads become working copies. Save still cannot write to the stick. */
  openDeviceProfile(
    deviceId: string,
    expectedGeneration: number,
    name: string,
  ): Promise<EditorSnapshot>;
  openDevicePreferences(deviceId: string, expectedGeneration: number): Promise<EditorSnapshot>;

  /** Capacity-one native live stream. Dispose is required and idempotent. */
  startLiveInput(onFrame: (frame: LiveSnapshot) => void): Promise<Subscription>;

  /** Invalidation only: callers re-query state instead of trusting event payloads. */
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
  if (isQcmCommandError(reason)) {
    return reason;
  }
  if (isErrorPayload(reason)) {
    return new QcmCommandError(reason);
  }
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
  if (typeof value !== "object" || value === null) {
    return false;
  }
  const candidate = value as Partial<QcmErrorPayload>;
  return typeof candidate.code === "string" && typeof candidate.message === "string";
}
