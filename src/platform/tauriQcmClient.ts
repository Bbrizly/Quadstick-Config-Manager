/**
 * The only frontend file allowed to import `@tauri-apps/*`.
 *
 * Every native call is named here. Domain payloads stay under `request`; Tauri
 * `Channel`s are top-level command arguments because they are transport, not
 * domain data.
 */

import { Channel, invoke } from "@tauri-apps/api/core";

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
  SaveReceipt,
  SettingsPatch,
  Subscription,
} from "./contracts";
import { asQcmError, type QcmClient } from "./qcmClient";

interface NativeSubscription {
  readonly subscriptionId: string;
}

async function call<T>(command: string, request?: unknown): Promise<T> {
  return callArgs<T>(command, request === undefined ? undefined : { request });
}

async function callArgs<T>(command: string, args?: Record<string, unknown>): Promise<T> {
  try {
    return await invoke<T>(command, args);
  } catch (reason) {
    throw asQcmError(reason);
  }
}

function disposal(command: string, subscriptionId: string): Subscription {
  let disposed = false;
  return {
    dispose(): void {
      if (disposed) {
        return;
      }
      disposed = true;
      // React effect cleanup cannot await. Native removal is idempotent; a
      // teardown transport failure is deliberately not promoted into UI state.
      void invoke(command, { subscriptionId }).catch(() => undefined);
    },
  };
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

  getProfileSnapshot(sessionId: string): Promise<EditorSnapshot> {
    return call<EditorSnapshot>("get_profile_snapshot", { sessionId });
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

  commitInstall(
    planId: string,
    confirmationId?: string,
    onProgress: (progress: InstallProgress) => void = () => undefined,
  ): Promise<InstallReceipt> {
    const progress = new Channel<InstallProgress>(onProgress);
    return callArgs<InstallReceipt>("commit_install", {
      request: { planId, confirmationId },
      progress,
    });
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

  async startLiveInput(onFrame: (frame: LiveSnapshot) => void): Promise<Subscription> {
    const onFrameChannel = new Channel<LiveSnapshot>(onFrame);
    const native = await callArgs<NativeSubscription>("start_live_input", {
      onFrame: onFrameChannel,
    });
    return disposal("stop_live_input", native.subscriptionId);
  }

  async subscribeDevicesChanged(
    onChanged: (event: DeviceInvalidation) => void,
  ): Promise<Subscription> {
    const onChangedChannel = new Channel<DeviceInvalidation>(onChanged);
    const native = await callArgs<NativeSubscription>("subscribe_devices_changed", {
      onChanged: onChangedChannel,
    });
    return disposal("unsubscribe_devices_changed", native.subscriptionId);
  }
}

/** Every command name this client calls, for the API ledger to be checked against. */
export const TAURI_COMMANDS = [
  "get_app_snapshot",
  "get_settings",
  "update_settings",
  "new_profile",
  "choose_and_open_profile",
  "get_profile_snapshot",
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
  "start_live_input",
  "stop_live_input",
  "subscribe_devices_changed",
  "unsubscribe_devices_changed",
] as const;
