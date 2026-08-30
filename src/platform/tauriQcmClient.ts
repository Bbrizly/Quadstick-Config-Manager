/**
 * The only frontend file allowed to import `@tauri-apps/*`.
 *
 * Every native call is named here, and every payload is nested under `request`
 * so malformed input is converted by Rust into the same stable error DTO as a
 * valid request that was refused.
 */

import { invoke } from "@tauri-apps/api/core";

import type {
  AppSettings,
  AppSnapshot,
  CloseDisposition,
  CloseOutcome,
  DeletePlan,
  DeleteReceipt,
  DeviceLibrarySnapshot,
  DevicePresenceSnapshot,
  EditorOp,
  EditorSnapshot,
  InstallPlan,
  InstallReceipt,
  SaveReceipt,
  SettingsPatch,
} from "./contracts";
import { asQcmError, type QcmClient } from "./qcmClient";

async function call<T>(command: string, request?: unknown): Promise<T> {
  try {
    return await invoke<T>(command, request === undefined ? undefined : { request });
  } catch (reason) {
    throw asQcmError(reason);
  }
}

export class TauriQcmClient implements QcmClient {
  getAppSnapshot(): Promise<AppSnapshot> {
    return call<AppSnapshot>("get_app_snapshot");
  }

  getSettings(): Promise<AppSettings> {
    return call<AppSettings>("get_settings");
  }

  updateSettings(expectedRevision: number, patch: SettingsPatch): Promise<AppSettings> {
    return call<AppSettings>("update_settings", { expectedRevision, patch });
  }

  newProfile(name: string): Promise<EditorSnapshot> {
    return call<EditorSnapshot>("new_profile", { name });
  }

  chooseAndOpenProfile(): Promise<EditorSnapshot | null> {
    return call<EditorSnapshot | null>("choose_and_open_profile");
  }

  applyEditorOps(
    sessionId: string,
    expectedRevision: number,
    ops: readonly EditorOp[],
  ): Promise<EditorSnapshot> {
    return call<EditorSnapshot>("apply_editor_ops", { sessionId, expectedRevision, ops });
  }

  undoEditor(sessionId: string, expectedRevision: number): Promise<EditorSnapshot> {
    return call<EditorSnapshot>("undo_editor", { sessionId, expectedRevision });
  }

  saveProfile(sessionId: string, expectedRevision: number): Promise<SaveReceipt> {
    return call<SaveReceipt>("save_profile", { sessionId, expectedRevision });
  }

  saveProfileAs(sessionId: string, expectedRevision: number): Promise<SaveReceipt | null> {
    return call<SaveReceipt | null>("save_profile_as", { sessionId, expectedRevision });
  }

  closeProfile(sessionId: string, disposition: CloseDisposition): Promise<CloseOutcome> {
    return call<CloseOutcome>("close_profile", { sessionId, disposition });
  }

  listDevices(): Promise<DevicePresenceSnapshot> {
    return call<DevicePresenceSnapshot>("list_devices");
  }

  refreshDevices(): Promise<DevicePresenceSnapshot> {
    return call<DevicePresenceSnapshot>("refresh_devices");
  }

  chooseDeviceFolder(): Promise<DevicePresenceSnapshot | null> {
    return call<DevicePresenceSnapshot | null>("choose_device_folder");
  }

  getDeviceLibrary(deviceId: string): Promise<DeviceLibrarySnapshot> {
    return call<DeviceLibrarySnapshot>("get_device_library", { deviceId });
  }

  prepareInstall(sessionId: string, deviceId: string): Promise<InstallPlan> {
    return call<InstallPlan>("prepare_install", { sessionId, deviceId });
  }

  commitInstall(planId: string, confirmationId?: string): Promise<InstallReceipt> {
    return call<InstallReceipt>("commit_install", { planId, confirmationId });
  }

  prepareDeleteDeviceProfile(
    deviceId: string,
    expectedGeneration: number,
    name: string,
  ): Promise<DeletePlan> {
    return call<DeletePlan>("prepare_delete_device_profile", {
      deviceId,
      expectedGeneration,
      name,
    });
  }

  commitDeleteDeviceProfile(planId: string, confirmationId: string): Promise<DeleteReceipt> {
    return call<DeleteReceipt>("commit_delete_device_profile", { planId, confirmationId });
  }

  openDeviceProfile(
    deviceId: string,
    expectedGeneration: number,
    name: string,
  ): Promise<EditorSnapshot> {
    return call<EditorSnapshot>("open_device_profile", { deviceId, expectedGeneration, name });
  }

  openDevicePreferences(deviceId: string, expectedGeneration: number): Promise<EditorSnapshot> {
    return call<EditorSnapshot>("open_device_preferences", { deviceId, expectedGeneration });
  }
}

/** Every command name this client calls, for the API ledger to be checked against. */
export const TAURI_COMMANDS = [
  "get_app_snapshot",
  "get_settings",
  "update_settings",
  "new_profile",
  "choose_and_open_profile",
  "apply_editor_ops",
  "undo_editor",
  "save_profile",
  "save_profile_as",
  "close_profile",
  "list_devices",
  "refresh_devices",
  "choose_device_folder",
  "get_device_library",
  "prepare_install",
  "commit_install",
  "prepare_delete_device_profile",
  "commit_delete_device_profile",
  "open_device_profile",
  "open_device_preferences",
] as const;
