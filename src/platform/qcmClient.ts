/**
 * The one boundary the UI talks to the native side through.
 *
 * Components never import `@tauri-apps/*`. They take a `QcmClient` and call
 * methods on it, so every screen can be rendered in a browser against
 * `MockQcmClient` with no Tauri present, and so the list of things the frontend
 * can ask the operating system for is exactly this file.
 *
 * Every profile-mutating call carries `expectedRevision`. If the native side
 * answers `QCM_PROFILE_REVISION_CONFLICT`, the caller refetches and either
 * reapplies a still-valid draft with the user's knowledge or shows the
 * conflict. Never last-write-wins silently.
 */

import type {
  AppSettings,
  AppSnapshot,
  CloseDisposition,
  CloseOutcome,
  EditorOp,
  EditorSnapshot,
  QcmErrorPayload,
  SaveReceipt,
  SettingsPatch,
} from "./contracts";

export interface QcmClient {
  /** Version, platform, what is wired, and the current settings. */
  getAppSnapshot(): Promise<AppSnapshot>;

  getSettings(): Promise<AppSettings>;

  /**
   * Change some settings. A value the app does not offer is refused with
   * `QCM_REQUEST_OUT_OF_RANGE`; nothing is rounded to the nearest legal one.
   */
  updateSettings(expectedRevision: number, patch: SettingsPatch): Promise<AppSettings>;

  /** A new profile from the built-in template, with nothing to undo. */
  newProfile(name: string): Promise<EditorSnapshot>;

  /**
   * Ask for a file and open it. The dialog is native and command-internal, so
   * no path reaches here. `null` means the user cancelled, which leaves
   * nothing open and is not an error.
   */
  chooseAndOpenProfile(): Promise<EditorSnapshot | null>;

  /**
   * Apply a batch of edits, all or nothing. A batch that cannot be applied in
   * full applies none of it.
   */
  applyEditorOps(
    sessionId: string,
    expectedRevision: number,
    ops: readonly EditorOp[],
  ): Promise<EditorSnapshot>;

  /** Undo one edit. Undo is itself a change: it dirties and moves the revision. */
  undoEditor(sessionId: string, expectedRevision: number): Promise<EditorSnapshot>;

  /** Save to the profile's current target, or fail with `NEEDS_SAVE_TARGET`. */
  saveProfile(sessionId: string, expectedRevision: number): Promise<SaveReceipt>;

  /** Save somewhere the user names now. `null` means they cancelled. */
  saveProfileAs(sessionId: string, expectedRevision: number): Promise<SaveReceipt | null>;

  /** Close under an explicit answer about unsaved work. */
  closeProfile(sessionId: string, disposition: CloseDisposition): Promise<CloseOutcome>;
}

/**
 * What a rejected call throws.
 *
 * An `Error` so a stack survives and an uncaught one is legible, carrying the
 * typed payload so recovery UI switches on `code` rather than reading prose.
 */
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

/**
 * Turn whatever a rejected call produced into the typed payload.
 *
 * The native side always answers with the DTO, so the fallback branch is for a
 * transport failure: a command that is not registered, or a window torn down
 * mid-call. Those are bugs in this app, which is why the fallback is `internal`
 * and not a made-up recoverable code.
 */
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
