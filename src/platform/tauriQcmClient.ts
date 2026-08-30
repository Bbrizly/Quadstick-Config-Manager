/**
 * The only file in the frontend allowed to import `@tauri-apps/*`.
 *
 * An import boundary test holds that line, and the oxlint config states it, so
 * a component that reaches for `invoke` fails the build rather than review.
 *
 * Every command is called with one `request` object. The native side reads that
 * object itself instead of letting the framework deserialize a typed argument,
 * which is what makes a malformed payload come back as a typed error rather
 * than a framework string.
 */

import { invoke } from "@tauri-apps/api/core";

import type {
  AppSettings,
  AppSnapshot,
  CloseDisposition,
  CloseOutcome,
  EditorOp,
  EditorSnapshot,
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
] as const;
